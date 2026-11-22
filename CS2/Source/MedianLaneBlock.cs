using System.Collections.Generic;
using Colossal.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;

namespace ConditionalMedians2
{
    // Takes the car connectors that cross a closed median out of the game's
    // route graph. The game's turn signs only make such a turn more expensive
    // (PathUtils.GetCarDriveSpecification adds a finite m_ForbiddenCost), so a
    // vehicle still takes it now and then. TimeActions move the edge's
    // connections in the graph from the junction's nodes to two dead-end nodes
    // of its own (UnsafePathfindData.SetEdgeDirections), so no route search can
    // use it (see Cut).
    // The graph is runtime data: it is rebuilt from the lanes on every load and
    // nothing of this is saved, so the city routes normally without the mod.
    // The game sends a lane's normal specification again whenever the lane
    // carries Updated or PathfindUpdated (LanesModifiedSystem), which
    // reconnects it, so this system runs right after LanesModifiedSystem and
    // cuts such lanes again. Releasing a lane tags it PathfindUpdated in the
    // main loop, before LaneDataSystem and LanesModifiedSystem run.
    public sealed partial class MedianLaneBlockSystem : GameSystemBase
    {
        private const int ReassertFrames = 256;
        private const float CutTime = 100000f;
        private MedianSystem medians;
        private PathfindQueueSystem queue;
        private EntityQuery closures;
        private EntityQuery segments;
        // Per closed junction: the lanes that have an edge in the route graph
        // (single and master lanes), and those plus the slaves of the masters,
        // which vehicles hold as their current and navigation lanes.
        private readonly Dictionary<Entity, HashSet<Entity>> graphLanes = new Dictionary<Entity, HashSet<Entity>>();
        private readonly Dictionary<Entity, HashSet<Entity>> vehicleLanes = new Dictionary<Entity, HashSet<Entity>>();
        private readonly HashSet<Entity> allVehicleLanes = new HashSet<Entity>();
        private readonly List<Entity> releases = new List<Entity>();
        private readonly List<Entity> toCut = new List<Entity>();
        private readonly List<Entity> gone = new List<Entity>();
        private readonly HashSet<Entity> closedNow = new HashSet<Entity>();
        private readonly HashSet<string> reportedExemptions = new HashSet<string>();
        private bool reapplyAll = true;
        private bool routesPending;
        private int framesToReassert = ReassertFrames;
        private int framesSinceLoad;

        // Bumped whenever the set of blocked vehicle lanes changes.
        internal int Version { get; private set; }
        internal HashSet<Entity> VehicleLanes => allVehicleLanes;
        // The cut lanes of one closed junction with their slaves, or null before
        // the junction has been classified.
        internal HashSet<Entity> VehicleLanesOf(Entity node) =>
            vehicleLanes.TryGetValue(node, out var lanes) ? lanes : null;
        internal IReadOnlyCollection<Entity> GraphLanes(Entity node) =>
            graphLanes.TryGetValue(node, out var lanes) ? (IReadOnlyCollection<Entity>)lanes : new Entity[0];

        protected override void OnCreate()
        {
            base.OnCreate();
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
            queue = World.GetOrCreateSystemManaged<PathfindQueueSystem>();
            closures = GetEntityQuery(ComponentType.ReadOnly<ClosedMedian>(), ComponentType.ReadOnly<SubLane>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            segments = GetEntityQuery(ComponentType.ReadOnly<Game.Routes.Segment>(), ComponentType.ReadWrite<PathTargets>(),
                ComponentType.ReadOnly<PathElement>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            // Entities of the previous city are gone and the graph is rebuilt.
            graphLanes.Clear();
            vehicleLanes.Clear();
            allVehicleLanes.Clear();
            releases.Clear();
            reportedExemptions.Clear();
            detoured.Clear();
            reapplyAll = true;
            routesPending = false;
            framesSinceLoad = 0;
            Version++;
        }

        protected override void OnUpdate()
        {
            framesSinceLoad++;
            // The game rebuilds every edge in the first update after a load;
            // cut again a little later as well, in case that came after ours.
            if (framesSinceLoad == 60 || framesSinceLoad == 300) reapplyAll = true;
            if (--framesToReassert <= 0)
            {
                framesToReassert = ReassertFrames;
                reapplyAll = true;
            }

            bool changed = false;
            closedNow.Clear();
            toCut.Clear();
            using (var nodes = closures.ToEntityArray(Allocator.Temp))
            foreach (var node in nodes)
            {
                closedNow.Add(node);
                // The geometry is read from the roads as the game left them this
                // frame, so connectors rebuilt in this frame, and those of the
                // first frame after a load, are tested against their own layout.
                if (!EntityManager.TryGetComponent<ClosedMedian>(node, out var placement) ||
                    (!medians.TryGeometry(node, out var geometry) &&
                     !medians.ActiveJunctions.TryGetValue(node, out geometry))) continue;
                var graph = new HashSet<Entity>();
                var vehicle = new HashSet<Entity>();
                Classify(node, geometry, placement, graph, vehicle);
                graphLanes.TryGetValue(node, out var oldGraph);
                vehicleLanes.TryGetValue(node, out var oldVehicle);
                foreach (var lane in graph)
                    if (reapplyAll || oldGraph == null || !oldGraph.Contains(lane) || Touched(lane)) toCut.Add(lane);
                if (oldGraph != null)
                    foreach (var lane in oldGraph)
                        if (!graph.Contains(lane)) releases.Add(lane);
                if (oldVehicle == null || !oldVehicle.SetEquals(vehicle)) changed = true;
                graphLanes[node] = graph;
                vehicleLanes[node] = vehicle;
            }

            // A junction that is no longer closed gives its lanes back.
            gone.Clear();
            foreach (var node in graphLanes.Keys) if (!closedNow.Contains(node)) gone.Add(node);
            foreach (var node in gone)
            {
                releases.AddRange(graphLanes[node]);
                graphLanes.Remove(node);
                vehicleLanes.Remove(node);
                changed = true;
            }

            if (changed)
            {
                allVehicleLanes.Clear();
                foreach (var lanes in vehicleLanes.Values) allVehicleLanes.UnionWith(lanes);
                Version++;
                routesPending = true;
            }
            reapplyAll = false;
            if (toCut.Count != 0) Cut(toCut);
        }

        // A lane whose graph edge the game sent again this frame.
        private bool Touched(Entity lane) =>
            EntityManager.HasComponent<Updated>(lane) || EntityManager.HasComponent<PathfindUpdated>(lane) ||
            EntityManager.HasComponent<Created>(lane);

        private void Classify(Entity node, MedianGeometry geometry, ClosedMedian placement,
            HashSet<Entity> graph, HashSet<Entity> vehicle)
        {
            var sublanes = EntityManager.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < sublanes.Length; i++)
            {
                Entity lane = sublanes[i].m_SubLane;
                if (EntityManager.HasComponent<SlaveLane>(lane) || EntityManager.HasComponent<Deleted>(lane) ||
                    !EntityManager.HasComponent<CarLane>(lane) ||
                    !EntityManager.TryGetComponent<Curve>(lane, out var curve) ||
                    !EntityManager.TryGetComponent<Lane>(lane, out var ends)) continue;
                bool master = EntityManager.TryGetComponent<MasterLane>(lane, out var group);
                bool crosses = geometry.CrossesGap(curve.m_Bezier);
                if (master)
                    for (int j = group.m_MinIndex; !crosses && j <= group.m_MaxIndex && j < sublanes.Length; j++)
                        crosses = EntityManager.TryGetComponent<Curve>(sublanes[j].m_SubLane, out var slave) &&
                            geometry.CrossesGap(slave.m_Bezier);
                if (!crosses) continue;
                // A lane from one approach of the divided road to the other runs
                // beside the median, never across it, even where a bend makes it
                // touch the line between the median ends.
                int from = ends.m_StartNode.GetOwnerIndex();
                int to = ends.m_EndNode.GetOwnerIndex();
                if (from != to && (from == placement.FirstRoad.Index || from == placement.SecondRoad.Index) &&
                    (to == placement.FirstRoad.Index || to == placement.SecondRoad.Index))
                {
                    if (reportedExemptions.Add($"{node.Index}/{lane.Index}"))
                        Mod.Log.Info($"Lane {lane.Index} at closed junction {node.Index} runs between the divided road's " +
                            "two approaches and touches the line between the median ends. It stays open.");
                    continue;
                }
                graph.Add(lane);
                vehicle.Add(lane);
                if (master)
                    for (int j = group.m_MinIndex; j <= group.m_MaxIndex && j < sublanes.Length; j++)
                        vehicle.Add(sublanes[j].m_SubLane);
            }
        }

        // A lane's edge is first switched off, which drops its connections at the
        // real start and end nodes, and then switched on again between two nodes
        // of its own that no other edge uses. No search can reach the edge, and
        // one that starts on it, for a car still in the turn, ends at once. An
        // edge left with no direction would keep start and end node IDs of -1,
        // a state the game never creates for a lane: coverage and availability
        // searches that start on it (CoverageJobs.AddSources,
        // AvailabilityJobs.AddProviders) do not check those IDs. The private
        // nodes are created by the connection and removed by the game when its
        // normal update reconnects the lane (UnsafePathfindData.AddConnection,
        // RemoveConnection).
        private static PathNode DeadEnd(Entity lane, int which) => new PathNode(lane, (ushort)(0xFFF0 + which));

        private void Cut(List<Entity> lanes)
        {
            var action = new TimeAction(Allocator.Persistent);
            int count = 0;
            foreach (var lane in lanes)
            {
                if (!EntityManager.TryGetComponent<Lane>(lane, out var ends)) continue;
                action.m_TimeData.Enqueue(new TimeActionData
                {
                    m_Owner = lane,
                    m_StartNode = ends.m_StartNode,
                    m_EndNode = ends.m_EndNode,
                    m_SecondaryStartNode = ends.m_StartNode,
                    m_SecondaryEndNode = ends.m_EndNode,
                    m_Flags = TimeActionFlags.SetPrimary | TimeActionFlags.SetSecondary,
                    m_Time = CutTime
                });
                action.m_TimeData.Enqueue(new TimeActionData
                {
                    m_Owner = lane,
                    m_StartNode = DeadEnd(lane, 0),
                    m_EndNode = DeadEnd(lane, 1),
                    m_SecondaryStartNode = DeadEnd(lane, 2),
                    m_SecondaryEndNode = DeadEnd(lane, 3),
                    m_Flags = TimeActionFlags.SetPrimary | TimeActionFlags.SetSecondary | TimeActionFlags.EnableForward,
                    m_Time = CutTime
                });
                count++;
            }
            if (count == 0)
            {
                action.Dispose();
                return;
            }
            // Modification actions run in the order they are queued, after the
            // updates LanesModifiedSystem queued earlier this frame.
            queue.Enqueue(action, default(JobHandle));
        }

        // Runs in the main loop before the frame's modifications: gives lanes
        // back to the graph and makes bus lines plan their route segments again.
        internal void Flush()
        {
            bool released = releases.Count != 0;
            foreach (var lane in releases)
                if (EntityManager.Exists(lane) && !EntityManager.HasComponent<Deleted>(lane))
                    EntityManager.AddComponent<PathfindUpdated>(lane);
            releases.Clear();
            // Route segments that went around a closed median plan again once a
            // turn is given back, so a line uses the reopened turn and its length
            // and duration are no longer the detour's.
            if (released && detoured.Count != 0)
            {
                foreach (var segment in detoured) Replan(segment);
                detoured.Clear();
            }
            if (!routesPending) return;
            routesPending = false;
            if (allVehicleLanes.Count == 0) return;
            using (var entities = segments.ToEntityArray(Allocator.Temp))
            foreach (var segment in entities)
            {
                var path = EntityManager.GetBuffer<PathElement>(segment, true);
                bool crosses = false;
                for (int i = 0; i < path.Length && !crosses; i++) crosses = allVehicleLanes.Contains(path[i].m_Target);
                if (!crosses) continue;
                Replan(segment);
                detoured.Add(segment);
            }
        }

        // Route segments planned again around a closed median.
        private readonly HashSet<Entity> detoured = new HashSet<Entity>();

        // RoutePathSystem plans a segment again only when its targets differ from
        // the waypoints' lanes (RoutePathSystem.SetupPathfind).
        private void Replan(Entity segment)
        {
            if (!EntityManager.Exists(segment) || EntityManager.HasComponent<Deleted>(segment) ||
                !EntityManager.TryGetComponent<PathTargets>(segment, out var targets)) return;
            targets.m_StartLane = Entity.Null;
            EntityManager.SetComponentData(segment, targets);
            EntityManager.AddComponent<Updated>(segment);
        }
    }

    // Vehicles drive the path they were given and only check that its lanes
    // still exist, so a path planned before a crossing was cut keeps it. This
    // makes such vehicles plan again while they can still choose another turn.
    // A vehicle that already holds a crossing lane (its current lane, the lane
    // it is changing to, or a reserved navigation lane, which the game keeps
    // when it plans again) is left to finish the turn.
    public sealed partial class MedianPathScanSystem : GameSystemBase
    {
        // Farther than a heavy vehicle reserves lanes ahead of itself at highway
        // speed, plus the way from a median's center to the stop line.
        private const float NearDistance = 400f;
        private const int NearElements = 64;
        private const int FullElements = 1024;
        private const uint RetryFrames = 256;
        private const int MaximumAttempts = 3;
        private const uint AttemptWindow = 3600;
        private MedianLaneBlockSystem blocks;
        private MedianSystem medians;
        private SimulationSystem simulation;
        private EntityQuery cars;
        private int seenVersion = -1;
        private uint changedAt;
        // The frame of the last re-route, the first frame and count of re-routes
        // in the current window, and the cut set they were made under.
        private readonly Dictionary<Entity, (uint Frame, uint First, int Count, int Version)> attempts =
            new Dictionary<Entity, (uint, uint, int, int)>();
        private readonly List<Entity> expired = new List<Entity>();
        private readonly List<float3> centers = new List<float3>();
        internal int Rerouted { get; private set; }
        internal int Committed { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();
            blocks = World.GetOrCreateSystemManaged<MedianLaneBlockSystem>();
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
            simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            cars = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadWrite<PathOwner>(), ComponentType.ReadOnly<PathElement>(),
                    ComponentType.ReadOnly<CarCurrentLane>(), ComponentType.ReadOnly<CarNavigationLane>(),
                    ComponentType.ReadOnly<UpdateFrame>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            });
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            attempts.Clear();
            seenVersion = -1;
        }

        protected override void OnUpdate()
        {
            var lanes = blocks.VehicleLanes;
            if (lanes.Count == 0) return;
            uint frame = simulation.frameIndex;
            if (blocks.Version != seenVersion)
            {
                seenVersion = blocks.Version;
                changedAt = frame;
            }
            // The cars of one update bucket per frame, as the game moves them, so
            // each car is checked once every 16 frames just before its navigation
            // update. All of them during the 16 frames after the cut lanes change
            // and again once searches that were already running have returned;
            // otherwise those near a closed junction.
            uint since = frame - changedAt;
            bool full = since < 16 || (since >= 64 && since < 80) || (since >= 512 && since < 528);
            centers.Clear();
            if (!full) foreach (var median in medians.Active) centers.Add(median.Center);
            cars.ResetFilter();
            cars.SetSharedComponentFilter(new UpdateFrame(frame & 15u));
            using (var entities = cars.ToEntityArray(Allocator.Temp))
            using (var positions = cars.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp))
                for (int i = 0; i < entities.Length; i++)
                {
                    if (!full && !Near(positions[i].m_Position)) continue;
                    Check(entities[i], lanes, frame, full ? FullElements : NearElements);
                }
            cars.ResetFilter();

            if ((frame & 4095) == 0)
            {
                expired.Clear();
                foreach (var pair in attempts) if (frame - pair.Value.First > AttemptWindow) expired.Add(pair.Key);
                foreach (var car in expired) attempts.Remove(car);
            }
        }

        private bool Near(float3 position)
        {
            foreach (var center in centers)
                if (math.distancesq(center.xz, position.xz) < NearDistance * NearDistance) return true;
            return false;
        }

        private const Game.Vehicles.CarLaneFlags Kept = Game.Vehicles.CarLaneFlags.Reserved |
            Game.Vehicles.CarLaneFlags.ClearedForPathfind;

        // Whether a car holds one of the lanes: its current lane, the lane it is
        // changing to, or one of the leading reserved navigation lanes, which the
        // game keeps when the car plans again (CarNavigationSystem.ClearNavigationLanes).
        internal static bool HoldsLane(EntityManager entities, Entity car, HashSet<Entity> lanes)
        {
            if (!entities.TryGetComponent<CarCurrentLane>(car, out var current)) return false;
            if (lanes.Contains(current.m_Lane) || lanes.Contains(current.m_ChangeLane)) return true;
            if (!entities.HasBuffer<CarNavigationLane>(car)) return false;
            var navigation = entities.GetBuffer<CarNavigationLane>(car, true);
            for (int i = 0; i < navigation.Length && (navigation[i].m_Flags & Kept) != 0; i++)
                if (lanes.Contains(navigation[i].m_Lane)) return true;
            return false;
        }

        private void Check(Entity car, HashSet<Entity> lanes, uint frame, int elements)
        {
            // A search in flight or a path just handed over is checked next time.
            var owner = EntityManager.GetComponentData<PathOwner>(car);
            const PathFlags busy = PathFlags.Pending | PathFlags.Obsolete | PathFlags.Updated | PathFlags.Failed |
                PathFlags.Stuck;
            if ((owner.m_State & busy) != 0) return;
            if (HoldsLane(EntityManager, car, lanes))
            {
                Committed++;
                return;
            }
            var navigation = EntityManager.GetBuffer<CarNavigationLane>(car, true);
            int reserved = 0;
            while (reserved < navigation.Length && (navigation[reserved].m_Flags & Kept) != 0) reserved++;
            bool uses = false;
            for (int i = reserved; i < navigation.Length && !uses; i++) uses = lanes.Contains(navigation[i].m_Lane);
            var path = EntityManager.GetBuffer<PathElement>(car, true);
            int start = math.max(0, owner.m_ElementIndex);
            int end = math.min(path.Length, start + elements);
            for (int i = start; i < end && !uses; i++) uses = lanes.Contains(path[i].m_Target);
            if (!uses) return;

            // A search on the cut graph cannot cross again, so a car that keeps
            // getting such a path under the same cut lanes means the cut did not
            // hold: stop asking. New cut lanes start the count again.
            attempts.TryGetValue(car, out var tries);
            bool newCuts = tries.Version != blocks.Version;
            if (!newCuts && tries.Count != 0 && frame - tries.Frame < RetryFrames) return;
            if (newCuts || frame - tries.First > AttemptWindow) tries = (frame, frame, 0, blocks.Version);
            if (tries.Count >= MaximumAttempts)
            {
                if (tries.Count == MaximumAttempts)
                {
                    Mod.Log.Warn($"Car {car.Index} was routed again {MaximumAttempts} times and still plans across a closed median. " +
                        "The mod leaves it alone.");
                    attempts[car] = (tries.Frame, tries.First, tries.Count + 1, tries.Version);
                }
                return;
            }
            attempts[car] = (frame, tries.First, tries.Count + 1, blocks.Version);
            // Obsolete does not clear the navigation lanes of a path that is being
            // appended to (CarNavigationSystem.UpdateNavigationLanes), so a bus
            // that skipped a stop drops Append too and plans its whole way again
            // from where it is (TransportCarAISystem.FindNewPath).
            owner.m_State = (owner.m_State & ~PathFlags.Append) | PathFlags.Obsolete;
            EntityManager.SetComponentData(car, owner);
            Rerouted++;
        }
    }
}
