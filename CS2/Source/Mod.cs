using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Modding;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using CarLane = Game.Net.CarLane;
using Hash128 = Unity.Entities.Hash128;
using SubLane = Game.Net.SubLane;
using TrackLane = Game.Net.TrackLane;

namespace ConditionalMedians2
{
    public sealed class Mod : IMod
    {
        internal static readonly ILog Log = LogManager.GetLogger("ConditionalMedians2");

        public void OnLoad(UpdateSystem updates)
        {
            updates.UpdateBefore<MedianSystem, ModificationSystem>(SystemUpdatePhase.MainLoop);
            updates.UpdateBefore<PrepareMedianSystem, CompositionSelectSystem>(SystemUpdatePhase.Modification3);
            // A divided side street at a closed T-junction keeps its own median
            // end: its junction composition is swapped after the game selects it
            // and before the composition, geometry and lane systems read it.
            updates.UpdateBefore<StemCompositionSystem, NetCompositionSystem>(SystemUpdatePhase.Modification4);
            updates.UpdateBefore<PrepareMedianRenderMeshSystem, NetCompositionMeshRefSystem>(SystemUpdatePhase.Modification4);
            updates.UpdateAfter<AlignMedianSystem, LaneSystem>(SystemUpdatePhase.Modification4);
            updates.UpdateAfter<MedianTextureSystem, Game.Rendering.BatchDataSystem>(SystemUpdatePhase.Rendering);
            // Crossing connectors leave the route graph after the game sends its lane updates.
            updates.UpdateAfter<MedianLaneBlockSystem, Game.Pathfind.LanesModifiedSystem>(SystemUpdatePhase.ModificationEnd);
            updates.UpdateBefore<MedianPathScanSystem, CarNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updates.UpdateAfter<VehicleDiagnostics, MedianSystem>(SystemUpdatePhase.MainLoop);
            // The approaches' median edge lines continue through closed junctions,
            // created before the lane search tree indexes the frame's new lanes and
            // disabled while a save writes the city.
            updates.UpdateBefore<MedianLineSystem, Game.Net.SearchSystem>(SystemUpdatePhase.Modification5);
            updates.UpdateBefore<MedianLineSaveHide, Game.Serialization.BeginPrefabSerializationSystem>(SystemUpdatePhase.Serialize);
            updates.UpdateAfter<MedianLineSaveRestore, Game.Serialization.WriteSystem>(SystemUpdatePhase.Serialize);
            // The median tool and the panel's bindings for the UI module.
            updates.UpdateAt<MedianToolSystem>(SystemUpdatePhase.ToolUpdate);
            updates.UpdateAt<MedianUISystem>(SystemUpdatePhase.UIUpdate);
            updates.UpdateAt<MedianTooltipSystem>(SystemUpdatePhase.UITooltip);
            Log.Info("Conditional Medians II native median tool loaded.");
        }

        public void OnDispose()
        {
            World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<MedianSystem>()?.ResetControls();
        }
    }

    public sealed partial class PrepareMedianSystem : GameSystemBase
    {
        private EntityQuery active;
        // Junctions that carry the game's upgrade flags but no closure record.
        private EntityQuery unclosed;
        private EntityQuery unclosedUpdated;
        private EntityQuery updatedRoads;
        private bool sweepAll = true;
        private MedianSystem medians;
        private AlignMedianSystem alignment;

        protected override void OnCreate()
        {
            base.OnCreate();
            active = GetEntityQuery(ComponentType.ReadOnly<ClosedMedian>(), ComponentType.Exclude<Deleted>());
            unclosed = GetEntityQuery(ComponentType.ReadOnly<Upgraded>(), ComponentType.ReadOnly<Game.Net.Node>(),
                ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.Exclude<ClosedMedian>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            unclosedUpdated = GetEntityQuery(ComponentType.ReadOnly<Upgraded>(), ComponentType.ReadOnly<Game.Net.Node>(),
                ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<Updated>(), ComponentType.Exclude<ClosedMedian>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            updatedRoads = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Updated>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
            alignment = World.GetOrCreateSystemManaged<AlignMedianSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            sweepAll = true;
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            sweepAll = true;
        }

        protected override void OnUpdate()
        {
            ClearUnrecordedMarks();
            alignment.CollectRenderMeshes();
            alignment.RetryBlockedStrips();
            alignment.RetryNativeFallbacks();
            using (var entities = active.ToEntityArray(Allocator.Temp))
            foreach (var node in entities)
            {
                var saved = EntityManager.GetComponentData<ClosedMedian>(node);
                // A painted median builds nothing: no closed bit, no road sections.
                if (medians.IsPainted(node))
                {
                    if (medians.CanMark(node)) medians.Mark(node, false);
                    continue;
                }
                medians.PrepareRoad(saved.FirstRoad);
                medians.PrepareRoad(saved.SecondRoad);
                medians.PrepareStems(node, saved);
                if (!medians.CanMark(node)) continue;
                // A loaded city selects the closed compositions again without
                // marking its roads as updated. Refresh a saved closure until
                // the alignment pass has reached it, so the curb fit runs too.
                bool refresh = medians.ConsumeReadyMesh(node) |
                    medians.ConsumeLoadRefresh(node, alignment.HasAligned(node)) |
                    medians.ConsumeRecheck(node) |
                    medians.ConsumeRefresh(node);
                // A new closure's median waits until its junction has drained.
                medians.Mark(node, medians.CanRenderMedian(node) && !medians.Draining(node), refresh);
            }
        }

        // The closed bit lives in the game's own upgrade flags, which a save keeps
        // even when this mod is not loaded, while the mod's closure record is
        // dropped. A city saved without the mod (or by an older build that left
        // such bits) therefore comes back with closed bits and no closures. The
        // game's own tools cannot create one: they mask a junction's upgrade
        // flags to the game's node flags. Another mod that writes upgrade flags
        // could. Such a bit would make the junction pick the closed median once
        // its road type is prepared, with no closure behind it. Clear every bit
        // that has no record: all junctions once after each load, then, every
        // frame, the updated junctions and both ends of the updated roads (the
        // game reselects a road's junction ends whenever the road is updated),
        // before the game selects their compositions this frame.
        private void ClearUnrecordedMarks()
        {
            // The full sweep stays on until the city has finished loading.
            bool loading = GameManager.instance == null || GameManager.instance.isGameLoading;
            int cleared = 0;
            if (sweepAll)
            {
                if (!unclosed.IsEmptyIgnoreFilter)
                    using (var nodes = unclosed.ToEntityArray(Allocator.Temp))
                        foreach (var node in nodes)
                            if (ClearIfUnrecorded(node)) cleared++;
                if (cleared != 0)
                    Mod.Log.Info($"Cleared {cleared} closed-median mark(s) with no closure record (left by a save made without this mod, an older build, or another mod). Those junctions are open.");
                if (!loading) sweepAll = false;
                return;
            }
            if (!unclosedUpdated.IsEmptyIgnoreFilter)
                using (var nodes = unclosedUpdated.ToEntityArray(Allocator.Temp))
                    foreach (var node in nodes)
                        if (ClearIfUnrecorded(node)) cleared++;
            if (!updatedRoads.IsEmptyIgnoreFilter)
                using (var roads = updatedRoads.ToComponentDataArray<Edge>(Allocator.Temp))
                    foreach (var road in roads)
                    {
                        if (ClearIfUnrecorded(road.m_Start)) cleared++;
                        if (ClearIfUnrecorded(road.m_End)) cleared++;
                    }
            if (cleared != 0)
                Mod.Log.Info($"Cleared {cleared} closed-median mark(s) with no closure record on updated junctions (written by another mod). Those junctions are open.");
        }

        private bool ClearIfUnrecorded(Entity node)
        {
            if (node == Entity.Null || !EntityManager.Exists(node) || !medians.CanMark(node) ||
                !EntityManager.HasComponent<Upgraded>(node) || EntityManager.HasComponent<ClosedMedian>(node) ||
                EntityManager.HasComponent<Temp>(node) || EntityManager.HasComponent<Deleted>(node) ||
                (EntityManager.GetComponentData<Upgraded>(node).m_Flags.m_General & NativeSections.Closed) == 0) return false;
            medians.Mark(node, false);
            return true;
        }
    }

    public sealed partial class PrepareMedianRenderMeshSystem : GameSystemBase
    {
        private AlignMedianSystem alignment;

        protected override void OnCreate()
        {
            base.OnCreate();
            alignment = World.GetOrCreateSystemManaged<AlignMedianSystem>();
        }

        protected override void OnUpdate() => alignment.PrepareRenderMeshes();
    }

    public sealed partial class MedianSystem : GameSystemBase
    {
        private EntityQuery placements;
        private EntityQuery oldPlacements;
        private EntityQuery cars;
        private NativeSections sections;
        private MedianLaneBlockSystem blocks;
        private Entity requested;
        private string status = "";
        private readonly Dictionary<Entity, MedianGeometry> active = new Dictionary<Entity, MedianGeometry>();
        private readonly Dictionary<Entity, Hash128> rejectedGeometry = new Dictionary<Entity, Hash128>();
        // Why each rejected closure's median is not drawn, for the panel.
        private readonly Dictionary<Entity, string> rejectionReasons = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, (Entity First, Entity Second)> pendingMedianMeshes =
            new Dictionary<Entity, (Entity, Entity)>();
        private readonly Dictionary<Entity, (Entity First, Entity Second)> pendingRenderMeshes =
            new Dictionary<Entity, (Entity, Entity)>();
        private readonly List<Entity> expired = new List<Entity>();
        private readonly HashSet<Entity> confirmed = new HashSet<Entity>();
        private readonly Dictionary<Entity, int> loadRefreshes = new Dictionary<Entity, int>();
        private const int MaximumLoadRefreshes = 3;
        private readonly Dictionary<Entity, int> rechecks = new Dictionary<Entity, int>();
        private readonly HashSet<Entity> pendingRechecks = new HashSet<Entity>();
        private const int MaximumRechecks = 3;
        private readonly HashSet<Entity> pendingRefreshes = new HashSet<Entity>();

        // A new closure blocks traffic at once, but its median is drawn only once
        // no vehicle still holds a crossing turn there, so no vehicle is seen
        // driving over the closed median. The state is kept in memory only: a
        // city saved meanwhile loads with the median drawn.
        private readonly Dictionary<Entity, Drain> draining = new Dictionary<Entity, Drain>();
        private readonly List<Entity> drained = new List<Entity>();
        private SimulationSystem simulation;
        private const uint DrainTimeout = 1800;
        private const int ClearChecks = 3;
        private const float DrainDistance = 80f;

        private struct Drain
        {
            internal uint Started;
            // Vehicles that held a crossing turn at the last check; -1 before the
            // junction's crossing turns are known.
            internal int Waiting;
            internal int Clear;
        }

        internal Dictionary<Entity, MedianGeometry>.ValueCollection Active => active.Values;
        internal Dictionary<Entity, MedianGeometry> ActiveJunctions => active;
        // The last thing a toggle did, for the panel.
        internal string Status => status;
        internal int ClosedCount => active.Count;
        internal bool Draining(Entity node) => draining.ContainsKey(node);
        // Whether the junction's closed median is drawn, natively or by the mod.
        internal bool DrawsMedian(Entity node) =>
            confirmed.Contains(node) && !draining.ContainsKey(node) && !rejectedGeometry.ContainsKey(node);
        // Closes or reopens a junction's median at the start of the next frame.
        internal void RequestToggle(Entity node) { requested = node; }

        // What the median tool shows for a junction under the cursor.
        // NoOpening: nothing to close here (a road end, a junction of undivided
        // roads, a divided road without an opening), shown quietly rather than as
        // a refusal, so sweeping along a road does not light every node red.
        internal enum JunctionState { NotJunction, NoOpening, Refused, Closable, Draining, Updating, Closed, NotDrawn }

        internal struct JunctionView
        {
            internal JunctionState State;
            internal string Title;
            internal string Detail;
            internal bool HasGeometry;
            internal MedianGeometry Geometry;
            // The permitted turns that cross the median and keep it from closing.
            internal Bezier4x3[] Crossings;
        }

        private readonly List<Bezier4x3> crossingCurves = new List<Bezier4x3>();

        internal JunctionView Describe(Entity node)
        {
            var view = new JunctionView();
            if (!CanMark(node))
            {
                view.State = JunctionState.NotJunction;
                view.Title = "Not a junction";
                return view;
            }
            if (EntityManager.HasComponent<ClosedMedian>(node))
            {
                view.HasGeometry = active.TryGetValue(node, out view.Geometry);
                if (draining.TryGetValue(node, out var drain))
                {
                    view.State = JunctionState.Draining;
                    view.Title = "Closed to traffic";
                    // The wait counts simulation frames, so it stands still while the game is paused.
                    int left = (int)math.ceil(math.max(0f, DrainTimeout - (simulation.frameIndex - drain.Started)) / 60f);
                    string wait = simulation.selectedSpeed <= 0f ? " The game is paused; the wait resumes with it." : $" At most {left} s more.";
                    view.Detail = (drain.Waiting > 0
                        ? $"The median appears once {drain.Waiting} vehicle{(drain.Waiting == 1 ? " leaves its" : "s leave their")} crossing turn."
                        : "The median appears once the junction is clear.") + wait;
                }
                else if (rejectedGeometry.ContainsKey(node))
                {
                    view.State = JunctionState.NotDrawn;
                    view.Title = "Closed to traffic, median not drawn";
                    view.Detail = rejectionReasons.TryGetValue(node, out var reason)
                        ? $"The road's own median does not fit here: {reason}."
                        : "The road's own median does not fit here.";
                }
                else if (confirmed.Contains(node) && view.HasGeometry && view.Geometry.Painted && !LinesCanDraw())
                {
                    view.State = JunctionState.NotDrawn;
                    view.Title = "Closed to traffic, line not drawn";
                    view.Detail = "This game version's save would keep the continued line, so it is not drawn. The turns across it stay removed.";
                }
                else if (confirmed.Contains(node))
                {
                    view.State = JunctionState.Closed;
                    view.Title = view.HasGeometry && view.Geometry.Painted ? "Painted median closed" : "Median closed";
                }
                else
                {
                    view.State = JunctionState.Updating;
                    view.Title = "Closing the median";
                    view.Detail = "Updating the road geometry.";
                }
                return view;
            }
            ClosedMedian placement = default;
            if (!TryLayout(node, ref placement, out var geometry))
            {
                view.State = layoutNeutral ? JunctionState.NoOpening : JunctionState.Refused;
                view.Title = layoutNeutral ? "No median opening here" : "Cannot close this junction";
                view.Detail = layoutRefusal;
                return view;
            }
            view.HasGeometry = true;
            view.Geometry = geometry;
            crossingCurves.Clear();
            if (!TurnRulesPermit(node, geometry, false, out string turns, crossingCurves))
            {
                view.State = JunctionState.Refused;
                view.Title = "Cannot close this junction yet";
                view.Detail = turns;
                view.Crossings = crossingCurves.ToArray();
                return view;
            }
            view.State = JunctionState.Closable;
            view.Title = geometry.Painted ? "Painted median" : "Median opening";
            if (geometry.Painted) view.Detail = "Closing continues its paint through the junction and removes the turns across it.";
            return view;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            sections = new NativeSections(EntityManager);
            blocks = World.GetOrCreateSystemManaged<MedianLaneBlockSystem>();
            simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            placements = GetEntityQuery(ComponentType.ReadOnly<ClosedMedian>(), ComponentType.Exclude<Deleted>());
            oldPlacements = GetEntityQuery(ComponentType.ReadOnly<MedianPlacement>());
            paintMarkingQuery = GetEntityQuery(MedianLines.MarkingQuery);
            cars = GetEntityQuery(ComponentType.ReadOnly<Game.Vehicles.Car>(), ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Objects.TripSource>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            ResetControls();
        }

        protected override void OnUpdate()
        {
            if (GameManager.instance == null || GameManager.instance.gameMode != GameMode.Game ||
                GameManager.instance.isGameLoading) return;
            if (!oldPlacements.IsEmptyIgnoreFilter) EntityManager.RemoveComponent<MedianPlacement>(oldPlacements);
            // Before the frame's modifications, so the game re-sends released lanes.
            blocks.Flush();
            Refresh();
            UpdateDrains();
            if (MedianDiagnostics.Poll())
            {
                // The diagnostics file changed. Fit every closure again with its settings.
                expired.Clear();
                expired.AddRange(active.Keys);
                MedianTextureSystem.DumpBatches = true;
                VehicleDiagnostics.Dump = true;
                foreach (var node in expired)
                {
                    DiagnoseLayout(node, "diagnostics changed");
                    Mark(node, CanRenderMedian(node) && !Draining(node), true);
                }
                expired.Clear();
            }
            if (requested != Entity.Null)
            {
                Entity node = requested;
                requested = Entity.Null;
                Toggle(node);
            }
        }

        // The junction the user toggled last: only its progress is reported as
        // the panel's last change, not every closure confirmed after a load.
        private Entity lastToggled;

        private void Toggle(Entity node)
        {
            lastToggled = node;
            if (!CanMark(node))
            {
                status = "That junction is gone.";
                return;
            }
            if (EntityManager.HasComponent<ClosedMedian>(node))
            {
                Remove(node);
                status = "Median opening restored.";
                return;
            }

            ClosedMedian placement = default;
            if (!TryLayout(node, ref placement, out var geometry))
            {
                status = layoutRefusal;
                Mod.Log.Info($"No compatible native median at junction {node.Index}: {layoutRefusal}");
                DiagnoseLayout(node, "toggle refused");
                return;
            }
            if (!TurnRulesPermit(node, geometry, true, out string turns))
            {
                status = turns;
                Mod.Log.Info($"Native median refused at junction {node.Index}: {turns}");
                return;
            }
            if (!geometry.Painted)
            {
                PrepareRoad(placement.FirstRoad);
                PrepareRoad(placement.SecondRoad);
                PrepareStems(node, placement);
            }
            // Traffic is cut at once; the median is drawn once the junction has drained.
            EntityManager.AddComponentData(node, placement);
            active[node] = geometry;
            draining[node] = new Drain { Started = simulation.frameIndex, Waiting = -1 };
            status = "Closed to traffic. The median appears once the junction is clear.";
        }

        // Draws the median of each new closure once no vehicle near it holds one
        // of its cut crossing turns for a few checks in a row, or after about 30
        // seconds of game time. Vehicles that hold such a turn finish it; the cut
        // keeps any other vehicle from entering one.
        private void UpdateDrains()
        {
            if (draining.Count == 0) return;
            drained.Clear();
            drained.AddRange(draining.Keys);
            foreach (var node in drained)
            {
                var drain = draining[node];
                var lanes = blocks.VehicleLanesOf(node);
                if (lanes != null && active.TryGetValue(node, out var geometry))
                {
                    drain.Waiting = CountHolders(geometry.Center, lanes);
                    drain.Clear = drain.Waiting == 0 ? drain.Clear + 1 : 0;
                }
                bool timedOut = simulation.frameIndex - drain.Started >= DrainTimeout;
                if (drain.Clear < ClearChecks && !timedOut)
                {
                    draining[node] = drain;
                    continue;
                }
                draining.Remove(node);
                if (timedOut && drain.Waiting != 0)
                    Mod.Log.Info($"Junction {node.Index}: drawing the closed median after the wait ran out, with " +
                        $"{(drain.Waiting < 0 ? "its crossing turns not yet known" : drain.Waiting + " vehicle(s) still in a crossing turn")}.");
                Mark(node, CanRenderMedian(node));
                if (node == lastToggled) status = "The junction is clear. Drawing the median.";
            }
        }

        private int CountHolders(float3 center, HashSet<Entity> lanes)
        {
            int count = 0;
            using (var entities = cars.ToEntityArray(Allocator.Temp))
            using (var positions = cars.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp))
                for (int i = 0; i < entities.Length; i++)
                    if (math.distancesq(center.xz, positions[i].m_Position.xz) < DrainDistance * DrainDistance &&
                        MedianPathScanSystem.HoldsLane(EntityManager, entities[i], lanes))
                        count++;
            return count;
        }

        private bool TurnRulesPermit(Entity node, MedianGeometry geometry, bool log, out string reason,
            List<Bezier4x3> crossings = null)
        {
            reason = null;
            if (!EntityManager.HasBuffer<SubLane>(node))
            {
                reason = "The junction has no lanes yet.";
                return false;
            }
            bool hasRoadLane = false;
            int crossing = 0;
            foreach (var item in EntityManager.GetBuffer<SubLane>(node, true))
            {
                Entity lane = item.m_SubLane;
                if (EntityManager.HasComponent<Deleted>(lane)) continue;
                if (EntityManager.HasComponent<TrackLane>(lane))
                {
                    reason = "Tram or train tracks run through this junction.";
                    return false;
                }
                if (!EntityManager.HasComponent<CarLane>(lane) || !EntityManager.HasComponent<Curve>(lane) ||
                    EntityManager.HasComponent<MasterLane>(lane)) continue;
                var flags = EntityManager.GetComponentData<CarLane>(lane).m_Flags;
                if ((flags & CarLaneFlags.Roundabout) != 0)
                {
                    reason = "This is a roundabout.";
                    return false;
                }
                if (!EntityManager.HasComponent<PrefabRef>(lane))
                {
                    reason = "The junction's lanes are still being built.";
                    return false;
                }
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (!EntityManager.HasComponent<CarLaneData>(prefab) ||
                    (EntityManager.GetComponentData<CarLaneData>(prefab).m_RoadTypes & RoadTypes.Car) == 0) continue;
                hasRoadLane = true;
                if ((flags & (CarLaneFlags.Forbidden | CarLaneFlags.Unsafe)) != 0) continue;
                var curve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
                // A built divider blocks any lane that would run over it. Paint
                // only stops the turns that cross its line, the lanes the
                // closure cuts (MedianLaneBlock): a lane that only runs over a
                // wide painted strip without crossing its middle is left alone.
                if (!(geometry.Painted ? geometry.CrossesGap(curve) : geometry.Hits(curve))) continue;
                crossing++;
                crossings?.Add(curve);
                if (log)
                    Mod.Log.Info($"Connector {lane.Index} ({flags}) crosses the median from {geometry.Local(curve.a)} to {geometry.Local(curve.d)}.");
            }
            if (crossing > 0)
            {
                reason = $"{crossing} permitted turn{(crossing == 1 ? " crosses" : "s cross")} the median. " +
                    "Forbid the turns across it first, with the game's turn signs or Traffic's lane connector.";
                return false;
            }
            if (!hasRoadLane) reason = "No car lanes run through this junction.";
            return hasRoadLane;
        }

        // Why the last layout read refused a junction, for the panel and the log,
        // and whether there was simply nothing to close.
        private string layoutRefusal;
        private bool layoutNeutral;
        // Whether the last refusal was only a road at the junction caught
        // mid-rebuild (a road being replaced or split this frame).
        private bool layoutBuilding;
        // Set while a closure whose road was split picks its new pair: it reads
        // the junction as a new closure would, but it already exists.
        private bool followingSplit;
        // Why the last attempt to follow a split failed, for the release log.
        private string followRefusal;

        private bool Refuse(string reason)
        {
            layoutRefusal = reason;
            layoutNeutral = false;
            layoutBuilding = false;
            return false;
        }

        private bool Nothing(string reason)
        {
            layoutRefusal = reason;
            layoutNeutral = true;
            layoutBuilding = false;
            return false;
        }

        private bool Building()
        {
            Refuse("The roads here are still being built.");
            layoutBuilding = true;
            return false;
        }

        // The geometry of a closed junction as its roads are now, for the lane cut.
        internal bool TryGeometry(Entity node, out MedianGeometry geometry)
        {
            geometry = default;
            if (!EntityManager.TryGetComponent<ClosedMedian>(node, out var placement)) return false;
            return TryLayout(node, ref placement, out geometry);
        }

        private bool TryLayout(Entity node, ref ClosedMedian placement, out MedianGeometry geometry)
        {
            geometry = default;
            layoutRefusal = null;
            layoutBuilding = false;
            if (!CanMark(node)) return Refuse("That junction is gone.");
            var edges = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            // A road replaced this frame (split by a road built onto it, or
            // removed) is still listed beside its replacement until the game's
            // references catch up: the junction is read once it has settled.
            foreach (var connection in edges)
                if (EntityManager.HasComponent<Deleted>(connection.m_Edge)) return Building();
            if (edges.Length < 2) return Nothing("This is a road end, not a junction.");
            if (edges.Length > 4) return Refuse("More than four roads meet here.");

            int count = 0;
            var arms = new Arm[4];
            foreach (var connection in edges)
            {
                Entity edge = connection.m_Edge;
                if (!EntityManager.HasComponent<Edge>(edge) || !EntityManager.HasComponent<Curve>(edge) ||
                    !EntityManager.HasComponent<PrefabRef>(edge) || !EntityManager.HasComponent<Composition>(edge) ||
                    EntityManager.HasComponent<Deleted>(edge)) return Building();
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (!EntityManager.HasComponent<RoadData>(prefab)) return Refuse("A path or track joins this junction.");
                var ends = EntityManager.GetComponentData<Edge>(edge);
                var composition = EntityManager.GetComponentData<Composition>(edge);
                if (!EntityManager.HasComponent<NetCompositionData>(composition.m_Edge)) return Building();
                var road = EntityManager.GetComponentData<NetCompositionData>(composition.m_Edge);
                if (!sections.CanClose(prefab, composition.m_Edge)) continue;
                if (!EntityManager.HasComponent<EdgeGeometry>(edge)) return Building();

                var nodeComposition = ends.m_Start == node ? composition.m_StartNode : composition.m_EndNode;
                if (!EntityManager.HasComponent<NetCompositionData>(nodeComposition)) return Building();
                var flags = EntityManager.GetComponentData<NetCompositionData>(nodeComposition).m_Flags.m_General;
                // The junction's own kind refuses it whichever road reports it.
                if ((flags & CompositionFlags.General.Roundabout) != 0) return Refuse("This is a roundabout.");
                if ((flags & CompositionFlags.General.LevelCrossing) != 0) return Refuse("A railway crosses this junction.");
                if ((flags & CompositionFlags.General.DeadEnd) != 0) return Nothing("This is a road end, not a junction.");

                var curve = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
                bool isStart = ends.m_Start == node;
                float3 tangent = math.normalizesafe(isStart ? curve.b - curve.a : curve.c - curve.d);
                // A road's own problem only refuses the junction once the road
                // turns out to be one of the two through approaches: a divided
                // side street at a T-junction keeps its median as it is, also
                // where it leaves the junction as a bridge or into a tunnel.
                var arm = new Arm { Edge = edge, Prefab = prefab, Direction = math.normalizesafe(tangent.xz) };
                if ((flags & CompositionFlags.General.MiddlePlatform) != 0)
                    arm.Problem = "The median holds a transit platform.";
                else if ((flags & CompositionFlags.General.Tunnel) != 0)
                    arm.Problem = "Junctions in tunnels are not supported.";
                else if ((flags & CompositionFlags.General.Elevated) != 0)
                    arm.Problem = "Elevated junctions are not supported.";
                else if (math.abs(tangent.y) > 0.08f || math.lengthsq(tangent.xz) < 0.9f)
                    arm.Problem = "The divided road is too steep at the junction (over about 8%).";
                foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(composition.m_Edge, true))
                {
                    if (!NativeSections.IsDivider(piece)) continue;
                    if (arm.Width > 0)
                    {
                        arm.Problem = arm.Problem ?? "The road has more than one median.";
                        continue;
                    }
                    arm.Width = piece.m_Size.x;
                    arm.Offset = piece.m_Offset.x * (isStart ? 1 : -1);
                    var shape = EntityManager.GetComponentData<EdgeGeometry>(edge);
                    var left = isStart ? shape.m_Start.m_Left.a : shape.m_End.m_Left.d;
                    var right = isStart ? shape.m_Start.m_Right.a : shape.m_End.m_Right.d;
                    arm.EndOfMedian = math.lerp(left, right, 0.5f + piece.m_Offset.x / road.m_Width);
                }
                if (arm.Width <= 0) arm.Problem = arm.Problem ?? "The road's median has no width.";
                arms[count++] = arm;
            }

            if (count < 2) return TryPaintedLayout(node, edges, ref placement, out geometry);
            Arm first = arms[0];
            Arm second = arms[1];
            if (count == 3)
            {
                // A T-junction whose side street is divided too: the through road
                // is the pair that lines up, or the pair a closure already holds.
                // The side street's median keeps its own end at the junction.
                if (edges.Length != 3)
                    return Refuse("More than one divided road meets here. A divided side street is supported only at a T-junction.");
                if (!ThroughPair(arms, placement, out first, out second))
                    return placement.FirstRoad != Entity.Null
                        ? Refuse("The roads at this junction changed.")
                        : Refuse("None of the divided roads here runs straight through the junction (within about 11 degrees).");
            }
            else if (count > 3)
                return Refuse("More than one divided road meets here. A divided side street is supported only at a T-junction.");
            if (first.Problem != null) return Refuse(first.Problem);
            if (second.Problem != null) return Refuse(second.Problem);
            if (first.Prefab != second.Prefab) return Refuse("The two divided roads are different road types.");
            if (math.dot(first.Direction, second.Direction) > -0.98f)
                return Refuse("The divided road bends at the junction (over about 11 degrees).");
            // A road upgrade such as grass swaps the divider for another piece on
            // one segment at a time. A closure that already exists keeps blocking
            // traffic while its two dividers differ, and the renderer leaves the
            // junction surface as it is until they match again. A new closure
            // needs matching dividers.
            bool matched = math.abs(first.Width - second.Width) <= 0.1f && math.abs(first.Offset + second.Offset) <= 0.2f;
            if (!matched && placement.FirstRoad == Entity.Null)
                return Refuse("The two sides have different medians. Give both segments the same median upgrade.");
            if (placement.FirstRoad != Entity.Null &&
                !((first.Edge == placement.FirstRoad && second.Edge == placement.SecondRoad) ||
                  (second.Edge == placement.FirstRoad && first.Edge == placement.SecondRoad)))
                return Refuse("The roads at this junction changed.");
            float2 gap = first.EndOfMedian.xz - second.EndOfMedian.xz;
            float gapLength = math.length(gap);
            if (gapLength < 0.5f) return Nothing("The median has no opening here.");
            if (math.abs(first.EndOfMedian.y - second.EndOfMedian.y) > 1f)
                return Refuse("The two median ends differ in height by more than 1 m.");
            placement = new ClosedMedian { FirstRoad = first.Edge, SecondRoad = second.Edge };
            geometry = new MedianGeometry
            {
                Center = (first.EndOfMedian + second.EndOfMedian) * 0.5f,
                Axis = gap / gapLength,
                HalfWidth = math.max(first.Width, second.Width) * 0.5f,
                HalfLength = gapLength * 0.5f
            };
            return true;
        }

        // The two of a T-junction's three divided roads that form its through
        // road: the closure's own pair when it has one, else the one pair that
        // lines up within about 11 degrees. At three roads at most one pair can.
        private static bool ThroughPair(Arm[] arms, ClosedMedian placement, out Arm first, out Arm second)
        {
            first = default;
            second = default;
            if (placement.FirstRoad != Entity.Null)
            {
                int found = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (arms[i].Edge != placement.FirstRoad && arms[i].Edge != placement.SecondRoad) continue;
                    if (found == 0) first = arms[i];
                    else second = arms[i];
                    found++;
                }
                return found == 2;
            }
            float best = -0.98f;
            bool any = false;
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                {
                    float dot = math.dot(arms[i].Direction, arms[j].Direction);
                    if (dot > best) continue;
                    best = dot;
                    first = arms[i];
                    second = arms[j];
                    any = true;
                }
            return any;
        }

        // A painted median: a two-way road without a built divider runs straight
        // through the junction, and its median is the paint between its two
        // directions (a center line, or a painted strip). Its position is read
        // from the road's own car lanes at the junction, between the innermost
        // lane of each direction, so asymmetric roads and left-hand traffic
        // work alike; its width is what those lanes leave between them. At a
        // crossroads of two such roads the wider road is the one closed.
        private bool TryPaintedLayout(Entity node, DynamicBuffer<ConnectedEdge> edges, ref ClosedMedian placement,
            out MedianGeometry geometry)
        {
            geometry = default;
            const string none = "No road with a median, built or painted, runs straight through this junction.";
            // A new closure, not the refresh of a closure that already exists.
            bool isNew = placement.FirstRoad == Entity.Null;
            // A closure following its split road picks its pair as a new one
            // would, but it exists: the new segment's lines may not be drawn yet
            // (its markings get their render data a frame or more later).
            bool checkNew = isNew && !followingSplit;
            if (edges.Length < 3) return Nothing(none);
            int count = 0;
            var arms = new PaintedArm[4];
            // Why a road here with a raised median of its own is not one this
            // mod closes, if no painted road is found instead.
            string raised = null;
            foreach (var connection in edges)
            {
                Entity edge = connection.m_Edge;
                if (!EntityManager.HasComponent<Edge>(edge) || !EntityManager.HasComponent<Curve>(edge) ||
                    !EntityManager.HasComponent<PrefabRef>(edge) || !EntityManager.HasComponent<Composition>(edge) ||
                    !EntityManager.HasComponent<EdgeGeometry>(edge) || EntityManager.HasComponent<Deleted>(edge))
                    return Building();
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                var ends = EntityManager.GetComponentData<Edge>(edge);
                var composition = EntityManager.GetComponentData<Composition>(edge);
                if (!EntityManager.HasComponent<NetCompositionData>(composition.m_Edge)) return Building();
                // A road with a built median is the raised case's; here it can only be a side street.
                if (sections.CanClose(prefab, composition.m_Edge)) continue;
                // Nor is a road with a raised divider the mod cannot close (two
                // medians side by side, a platform) painted, whatever lines it has.
                int blockers = 0;
                foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(composition.m_Edge, true))
                    if (NativeSections.IsRaisedBlocker(piece)) blockers++;
                if (blockers > 0)
                {
                    raised = raised ?? (blockers > 1 ? "The road has more than one median."
                        : "The road's median is not one this mod can close.");
                    continue;
                }
                bool isStart = ends.m_Start == node;
                var nodeComposition = isStart ? composition.m_StartNode : composition.m_EndNode;
                if (!EntityManager.HasComponent<NetCompositionData>(nodeComposition)) return Building();
                var flags = EntityManager.GetComponentData<NetCompositionData>(nodeComposition).m_Flags.m_General;
                // Undivided roads meet everywhere, so what rules a painted
                // median out here is shown quietly (grey, with the reason),
                // not as a refusal.
                if ((flags & CompositionFlags.General.Roundabout) != 0) return Nothing("This is a roundabout.");
                if ((flags & CompositionFlags.General.LevelCrossing) != 0) return Nothing("A railway crosses this junction.");
                if ((flags & CompositionFlags.General.DeadEnd) != 0) return Nothing("This is a road end, not a junction.");
                var curve = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
                float3 tangent = math.normalizesafe(isStart ? curve.b - curve.a : curve.c - curve.d);
                var arm = new PaintedArm
                {
                    Edge = edge,
                    Prefab = prefab,
                    Direction = math.normalizesafe(tangent.xz),
                    Width = EntityManager.GetComponentData<NetCompositionData>(composition.m_Edge).m_Width
                };
                // The direction at the junction mouth, as the line continuation
                // measures the approach; on a curve it differs from Direction,
                // which is taken at the junction's center.
                if (!MedianLines.Outward(EntityManager, edge, node, out arm.Outward) ||
                    !PaintedCenter(edge, isStart, arm.Outward, out arm.Center, out arm.HalfWidth)) continue;
                if ((flags & CompositionFlags.General.Tunnel) != 0) arm.Problem = "Junctions in tunnels are not supported.";
                else if ((flags & CompositionFlags.General.Elevated) != 0) arm.Problem = "Elevated junctions are not supported.";
                else if (math.abs(tangent.y) > 0.08f || math.lengthsq(tangent.xz) < 0.9f)
                    arm.Problem = "The road is too steep at the junction (over about 8%).";
                arms[count++] = arm;
            }
            if (count < 2) return Nothing(raised ?? none);

            // The closure's own pair, or else the pair that lines up; of two such
            // pairs (a crossroads) the wider road, and none if they are as wide.
            PaintedArm first = default, second = default;
            int pairs = 0;
            float best = -1f;
            bool tie = false;
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                {
                    if (placement.FirstRoad != Entity.Null)
                    {
                        if ((arms[i].Edge == placement.FirstRoad && arms[j].Edge == placement.SecondRoad) ||
                            (arms[j].Edge == placement.FirstRoad && arms[i].Edge == placement.SecondRoad))
                        {
                            first = arms[i];
                            second = arms[j];
                            pairs = 1;
                        }
                        continue;
                    }
                    if (math.dot(arms[i].Direction, arms[j].Direction) > -0.98f) continue;
                    pairs++;
                    float width = math.min(arms[i].Width, arms[j].Width);
                    if (width > best + 0.01f)
                    {
                        best = width;
                        first = arms[i];
                        second = arms[j];
                        tie = false;
                    }
                    else if (width > best - 0.01f) tie = true;
                }
            if (pairs == 0)
                return placement.FirstRoad != Entity.Null ? Refuse("The roads at this junction changed.") : Nothing(raised ?? none);
            if (tie)
                return Nothing("Both roads at this crossroads have a painted median and the same width, so it is not clear which one to close.");
            if (first.Problem != null) return Nothing(first.Problem);
            if (second.Problem != null) return Nothing(second.Problem);
            if (first.Prefab != second.Prefab) return Nothing("The two sides of the road are different road types.");
            if (math.dot(first.Direction, second.Direction) > -0.98f)
                return Nothing("The road bends at the junction (over about 11 degrees).");
            float2 gap = first.Center.xz - second.Center.xz;
            float gapLength = math.length(gap);
            if (gapLength < 0.5f) return Nothing("The painted median has no opening here.");
            if (math.abs(first.Center.y - second.Center.y) > 1f)
                return Nothing("The two sides of the road differ in height by more than 1 m.");
            var painted = new MedianGeometry
            {
                Painted = true,
                Center = (first.Center + second.Center) * 0.5f,
                Axis = gap / gapLength,
                HalfWidth = math.max(first.HalfWidth, second.HalfWidth),
                HalfLength = gapLength * 0.5f
            };
            // A new closure needs paint to continue: a line on the same side of
            // the median on both approaches, as the continuation joins them. A
            // closure that exists keeps going while a line is briefly hidden or
            // rebuilt.
            if (checkNew && !HasPaintToContinue(first, second, painted))
                return Nothing("This road has no center line or painted strip to continue through the junction.");
            // The continued line is all a painted closure draws; where the line
            // pass cannot run, a new one would only cut turns.
            if (checkNew && !LinesCanDraw())
                return Nothing("Painted medians are off in this game version: its save would keep the continued lines.");
            placement = new ClosedMedian { FirstRoad = first.Edge, SecondRoad = second.Edge };
            geometry = painted;
            return true;
        }

        private readonly MedianLines.Source[] paintFirst = new MedianLines.Source[2];
        private readonly MedianLines.Source[] paintSecond = new MedianLines.Source[2];
        private readonly bool[] paintHasFirst = new bool[2];
        private readonly bool[] paintHasSecond = new bool[2];
        private readonly List<Entity> paintFirstMarkings = new List<Entity>();
        private readonly List<Entity> paintSecondMarkings = new List<Entity>();
        // The markings an approach owns, read as the line continuation reads
        // them: by owner, since a mod that draws its own lines (Curbside) gives
        // them the road as owner without listing them among the road's lanes.
        private EntityQuery paintMarkingQuery;
        // The tool asks every frame it hovers a junction; the answer for the
        // same two roads is kept for a few frames.
        private const int PaintCheckFrames = 15;
        private Entity paintCheckFirst, paintCheckSecond;
        private int paintCheckFrame = int.MinValue;
        private bool paintCheckResult;

        private MedianLineSystem lineSystem;

        private bool LinesCanDraw()
        {
            if (lineSystem == null) lineSystem = World.GetExistingSystemManaged<MedianLineSystem>();
            return lineSystem == null || lineSystem.CanDraw;
        }

        private bool HasPaintToContinue(PaintedArm first, PaintedArm second, MedianGeometry painted)
        {
            int frame = UnityEngine.Time.frameCount;
            if (first.Edge == paintCheckFirst && second.Edge == paintCheckSecond &&
                frame - paintCheckFrame >= 0 && frame - paintCheckFrame < PaintCheckFrames) return paintCheckResult;
            paintFirstMarkings.Clear();
            paintSecondMarkings.Clear();
            using (var entities = paintMarkingQuery.ToEntityArray(Allocator.Temp))
            using (var owners = paintMarkingQuery.ToComponentDataArray<Owner>(Allocator.Temp))
                for (int i = 0; i < entities.Length; i++)
                {
                    if (owners[i].m_Owner == first.Edge) paintFirstMarkings.Add(entities[i]);
                    else if (owners[i].m_Owner == second.Edge) paintSecondMarkings.Add(entities[i]);
                }
            bool result = PaintSources(first, painted, paintFirstMarkings, paintFirst, paintHasFirst) &&
                PaintSources(second, painted, paintSecondMarkings, paintSecond, paintHasSecond) &&
                ((paintHasFirst[0] && paintHasSecond[0]) || (paintHasFirst[1] && paintHasSecond[1]));
            paintCheckFirst = first.Edge;
            paintCheckSecond = second.Edge;
            paintCheckFrame = frame;
            paintCheckResult = result;
            return result;
        }

        // The approach's center markings, found as the line continuation finds them.
        private bool PaintSources(PaintedArm arm, MedianGeometry painted, List<Entity> markings,
            MedianLines.Source[] found, bool[] has)
        {
            has[0] = has[1] = false;
            if (!EntityManager.TryGetComponent<Curve>(arm.Edge, out var road)) return false;
            float x = painted.Local(MathUtils.Position(road.m_Bezier, 0.5f)).x;
            if (math.abs(x) < 0.5f) return false;
            MedianLines.FindSources(EntityManager, markings, math.sign(x), arm.Outward, painted, found, has);
            return true;
        }

        // Where a two-way road's two directions meet at its junction end: the
        // point between the innermost car lane of each direction, and half the
        // painted width between those lanes (at least 10 cm for a line). False
        // for a one-way road, or one whose directions interleave.
        private bool PaintedCenter(Entity edge, bool isStart, float2 outward, out float3 center, out float halfWidth)
        {
            center = default;
            halfWidth = 0f;
            if (!EntityManager.HasBuffer<SubLane>(edge)) return false;
            var shape = EntityManager.GetComponentData<EdgeGeometry>(edge);
            float3 left = isStart ? shape.m_Start.m_Left.a : shape.m_End.m_Left.d;
            float3 right = isStart ? shape.m_Start.m_Right.a : shape.m_End.m_Right.d;
            float3 middle = math.lerp(left, right, 0.5f);
            float2 across = math.normalizesafe((right - left).xz);
            float inLow = float.MaxValue, inHigh = float.MinValue, outLow = float.MaxValue, outHigh = float.MinValue;
            float inLowWidth = 0f, inHighWidth = 0f, outLowWidth = 0f, outHighWidth = 0f;
            foreach (var item in EntityManager.GetBuffer<SubLane>(edge, true))
            {
                Entity lane = item.m_SubLane;
                if (!EntityManager.HasComponent<CarLane>(lane) || EntityManager.HasComponent<MasterLane>(lane) ||
                    EntityManager.HasComponent<Deleted>(lane) || !EntityManager.TryGetComponent<Curve>(lane, out var laneCurve) ||
                    !EntityManager.TryGetComponent<PrefabRef>(lane, out var lanePrefab) ||
                    !EntityManager.TryGetComponent<CarLaneData>(lanePrefab.m_Prefab, out var carData) ||
                    (carData.m_RoadTypes & RoadTypes.Car) == 0) continue;
                var bezier = laneCurve.m_Bezier;
                float3 end = math.distancesq(bezier.a.xz, middle.xz) < math.distancesq(bezier.d.xz, middle.xz) ? bezier.a : bezier.d;
                // Only lanes that end at this junction: a long edge has a second
                // set of lanes on its far half, ending at the edge's middle.
                if (math.abs(math.dot(end.xz - middle.xz, outward)) > 1f) continue;
                float lateral = math.dot(end.xz - middle.xz, across);
                float width = EntityManager.TryGetComponent<NetLaneData>(lanePrefab.m_Prefab, out var laneData) ? laneData.m_Width : 3f;
                bool incoming = math.dot((bezier.d - bezier.a).xz, outward) < 0f;
                if (incoming)
                {
                    if (lateral < inLow) { inLow = lateral; inLowWidth = width; }
                    if (lateral > inHigh) { inHigh = lateral; inHighWidth = width; }
                }
                else
                {
                    if (lateral < outLow) { outLow = lateral; outLowWidth = width; }
                    if (lateral > outHigh) { outHigh = lateral; outHighWidth = width; }
                }
            }
            if (inHigh == float.MinValue || outHigh == float.MinValue) return false;
            float split, between, widths;
            if (inHigh < outLow)
            {
                split = (inHigh + outLow) * 0.5f;
                between = outLow - inHigh;
                widths = (inHighWidth + outLowWidth) * 0.5f;
            }
            else if (outHigh < inLow)
            {
                split = (outHigh + inLow) * 0.5f;
                between = inLow - outHigh;
                widths = (outHighWidth + inLowWidth) * 0.5f;
            }
            else return false;
            halfWidth = math.max(0.1f, (between - widths) * 0.5f);
            center = middle + new float3(across.x, 0f, across.y) * split;
            return true;
        }

        private struct PaintedArm
        {
            internal Entity Edge;
            internal Entity Prefab;
            internal float2 Direction;
            internal float2 Outward;
            internal float Width;
            internal float3 Center;
            internal float HalfWidth;
            internal string Problem;
        }

        // Whether a closure's median is painted: its first road has no built
        // divider. Read from the road's composition when it has one, else from
        // its road type (on the frame a city loads, before compositions exist).
        internal bool IsPainted(Entity node)
        {
            if (active.TryGetValue(node, out var known)) return known.Painted;
            if (!EntityManager.TryGetComponent<ClosedMedian>(node, out var placement) ||
                !EntityManager.TryGetComponent<PrefabRef>(placement.FirstRoad, out var prefab)) return false;
            if (EntityManager.TryGetComponent<Composition>(placement.FirstRoad, out var composition) &&
                composition.m_Edge != Entity.Null && EntityManager.HasBuffer<NetCompositionPiece>(composition.m_Edge))
                return !sections.CanClose(prefab.m_Prefab, composition.m_Edge);
            return !sections.HasDivider(prefab.m_Prefab);
        }

        private struct Arm
        {
            internal Entity Edge;
            internal Entity Prefab;
            internal float2 Direction;
            internal float Width;
            internal float Offset;
            internal float3 EndOfMedian;
            // Why this road cannot be a closed median's approach, if it cannot.
            internal string Problem;
        }

        internal void PrepareRoad(Entity edge)
        {
            if (!EntityManager.HasComponent<PrefabRef>(edge)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            if (EntityManager.HasBuffer<NetGeometrySection>(prefab)) sections.Prepare(prefab);
        }

        // Prepares the road type of every side street at a closed junction
        // before the junction is marked closed. The game builds and keeps a
        // composition for every new mask from the road type's sections as they
        // are then, so a divided side street's closed-junction composition must
        // be built from prepared sections. StemCompositionSystem then moves that
        // end to a composition marked as a stem, which keeps its own median end.
        // The decision rests on the road type alone: a road's selected
        // composition is empty on the frame a city loads, and still the old one
        // on the frame a side street is replaced. Road types without a median
        // the mod can close are left as they are.
        internal void PrepareStems(Entity node, ClosedMedian placement)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return;
            using (var edges = EntityManager.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Allocator.Temp))
            foreach (var item in edges)
            {
                Entity edge = item.m_Edge;
                if (edge == placement.FirstRoad || edge == placement.SecondRoad ||
                    !EntityManager.HasComponent<PrefabRef>(edge)) continue;
                if (EntityManager.HasComponent<RoadData>(EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab))
                    PrepareRoad(edge);
            }
        }

        // Whether a road type carries the closed median variant, which a side
        // street of that type must not select at a closed junction.
        internal bool IsPreparedRoad(Entity prefab) => sections.IsPrepared(prefab);

        internal bool CanMark(Entity node)
        {
            return EntityManager.HasComponent<Game.Net.Node>(node) &&
                EntityManager.HasBuffer<ConnectedEdge>(node) && !EntityManager.HasComponent<Deleted>(node);
        }

        internal bool CanRenderMedian(Entity node) => !rejectedGeometry.ContainsKey(node);

        internal void WaitForMedianMesh(Entity node, Entity first, Entity second) =>
            pendingMedianMeshes[node] = (first, second);

        internal void WaitForMedianRenderMesh(Entity node, Entity first, Entity second) =>
            pendingRenderMeshes[node] = (first, second);

        internal bool ConsumeReadyMesh(Entity node)
        {
            bool piecesReady = pendingMedianMeshes.TryGetValue(node, out var pieces) &&
                EntityManager.HasBuffer<MeshVertex>(pieces.First) && EntityManager.HasBuffer<MeshVertex>(pieces.Second);
            bool renderReady = pendingRenderMeshes.TryGetValue(node, out var render) &&
                HasRenderMesh(render.First) && HasRenderMesh(render.Second);
            if (piecesReady) pendingMedianMeshes.Remove(node);
            if (renderReady) pendingRenderMeshes.Remove(node);
            return piecesReady || renderReady;
        }

        // Requests a refresh for a closure the alignment pass has not reached
        // since the city was loaded. The count bounds the retries for a closure
        // whose roads can never be read.
        internal bool ConsumeLoadRefresh(Entity node, bool aligned)
        {
            if (aligned) return false;
            loadRefreshes.TryGetValue(node, out int count);
            if (count >= MaximumLoadRefreshes) return false;
            loadRefreshes[node] = count + 1;
            return true;
        }

        private bool HasRenderMesh(Entity composition) =>
            EntityManager.HasComponent<NetCompositionMeshRef>(composition) &&
            EntityManager.GetComponentData<NetCompositionMeshRef>(composition).m_Mesh != Entity.Null;

        internal bool HasRejectedGeometry(Entity node, Hash128 inputs) =>
            rejectedGeometry.TryGetValue(node, out var previous) && previous.Equals(inputs);

        // True while a junction's inputs are the ones its fit was rejected for. A
        // rejection that repeats on settled inputs needs no further recheck.
        internal bool KeepsRejection(Entity node, Hash128 inputs)
        {
            if (!HasRejectedGeometry(node, inputs)) return false;
            rechecks.Remove(node);
            return true;
        }

        // Whether a new rejection still waits for its check on settled lanes.
        internal bool ConsumeRecheck(Entity node) => pendingRechecks.Remove(node);

        // Asks for one more fit of a junction on the next frame, with its roads
        // laid anew by the game.
        internal void RefreshJunction(Entity node) => pendingRefreshes.Add(node);

        internal bool ConsumeRefresh(Entity node) => pendingRefreshes.Remove(node);

        internal void ReportGeometry(Entity node, Hash128 inputs, string reason)
        {
            if (reason == null)
            {
                rechecks.Remove(node);
                pendingRechecks.Remove(node);
                if (rejectedGeometry.Remove(node)) confirmed.Remove(node);
                rejectionReasons.Remove(node);
                return;
            }
            if (HasRejectedGeometry(node, inputs)) return;
            rejectedGeometry[node] = inputs;
            rejectionReasons[node] = reason;
            confirmed.Remove(node);
            // The game rebuilds a junction's lanes at the end of the frame in
            // which its roads change. A fit made in that frame read the lanes of
            // the previous layout, for example those of a narrower median before
            // a grass upgrade. Check a new rejection again once they have settled.
            rechecks.TryGetValue(node, out int count);
            if (count < MaximumRechecks)
            {
                rechecks[node] = count + 1;
                pendingRechecks.Add(node);
            }
            if (node == lastToggled) status = $"Closed to traffic, but the road's own median does not fit here: {reason}.";
            Mod.Log.Info($"Median rendering skipped at junction {node.Index}: {reason}.");
        }

        internal void Mark(Entity node, bool closed, bool refreshGeometry = false)
        {
            if (!CanMark(node)) return;
            // A painted median has no closed variant: its junction keeps the open bit.
            if (closed && active.TryGetValue(node, out var known) && known.Painted) closed = false;
            var upgrade = EntityManager.HasComponent<Upgraded>(node)
                ? EntityManager.GetComponentData<Upgraded>(node) : default;
            var current = upgrade.m_Flags.m_General;
            upgrade.m_Flags.m_General = closed ? current | NativeSections.Closed : current & ~NativeSections.Closed;
            if (upgrade.m_Flags.m_General != current)
            {
                if (upgrade.m_Flags == default) EntityManager.RemoveComponent<Upgraded>(node);
                else if (EntityManager.HasComponent<Upgraded>(node)) EntityManager.SetComponentData(node, upgrade);
                else EntityManager.AddComponentData(node, upgrade);
            }
            else if (!refreshGeometry) return;
            using (var edges = EntityManager.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Allocator.Temp))
            {
                EntityManager.AddComponent<Updated>(node);
                foreach (var item in edges)
                    if (EntityManager.Exists(item.m_Edge)) EntityManager.AddComponent<Updated>(item.m_Edge);
            }
        }

        private readonly HashSet<Entity> diagnosedPrefabs = new HashSet<Entity>();

        private static string Flags(CompositionFlags flags) =>
            $"{(uint)flags.m_General:X8}/{(uint)flags.m_Left:X8}/{(uint)flags.m_Right:X8}";

        // Records what the game selected for each road at a junction whose
        // layout the mod cannot use, and how the road prefab defines its median.
        private void DiagnoseLayout(Entity node, string context)
        {
            if (!MedianDiagnostics.Enabled || !EntityManager.HasBuffer<ConnectedEdge>(node)) return;
            Mod.Log.Info($"DIAG layout junction {node.Index}: {context}");
            using (var edges = EntityManager.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Allocator.Temp))
            foreach (var item in edges)
            {
                Entity edge = item.m_Edge;
                if (!EntityManager.HasComponent<Edge>(edge) || !EntityManager.HasComponent<Composition>(edge) ||
                    !EntityManager.HasComponent<PrefabRef>(edge))
                {
                    Mod.Log.Info($"DIAG layout edge {edge.Index}: incomplete");
                    continue;
                }
                var ends = EntityManager.GetComponentData<Edge>(edge);
                var composition = EntityManager.GetComponentData<Composition>(edge);
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                bool starts = ends.m_Start == node;
                Entity nodeComposition = starts ? composition.m_StartNode : composition.m_EndNode;
                string upgraded = EntityManager.HasComponent<Upgraded>(edge)
                    ? Flags(EntityManager.GetComponentData<Upgraded>(edge).m_Flags) : "none";
                Mod.Log.Info($"DIAG layout edge {edge.Index}: prefab {prefab.Index} starts {starts} upgraded {upgraded} " +
                    $"canClose {sections.CanClose(prefab, composition.m_Edge)} roadData {EntityManager.HasComponent<RoadData>(prefab)} " +
                    $"updated {EntityManager.HasComponent<Updated>(edge)}");
                // The junction shape the game holds now, which the mod's last fit wrote unless the game rebuilt it.
                if (starts ? EntityManager.HasComponent<StartNodeGeometry>(edge) : EntityManager.HasComponent<EndNodeGeometry>(edge))
                {
                    var held = starts ? EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry
                        : EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry;
                    Mod.Log.Info($"DIAG layout edge {edge.Index} held geometry: {MedianDiagnostics.G(held)}");
                }
                DiagnosePieces($"edge {edge.Index} edge composition", composition.m_Edge);
                DiagnosePieces($"edge {edge.Index} node composition", nodeComposition);
                DiagnoseDrawnMesh($"edge {edge.Index} node composition", nodeComposition);
                DiagnosePrefab(prefab);
            }
        }

        private void DiagnosePieces(string label, Entity composition)
        {
            if (!EntityManager.HasBuffer<NetCompositionPiece>(composition) ||
                !EntityManager.HasComponent<NetCompositionData>(composition))
            {
                Mod.Log.Info($"DIAG layout {label} {composition.Index}: no pieces");
                return;
            }
            var data = EntityManager.GetComponentData<NetCompositionData>(composition);
            var text = new System.Text.StringBuilder();
            text.Append($"DIAG layout {label} {composition.Index}: flags {Flags(data.m_Flags)} width {MedianDiagnostics.N(data.m_Width)}");
            foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(composition, true))
                text.Append($" | piece {piece.m_Piece.Index} section {piece.m_SectionIndex} [{piece.m_SectionFlags}] [{piece.m_PieceFlags}] " +
                    $"size{MedianDiagnostics.V(piece.m_Size)} offset{MedianDiagnostics.V(piece.m_Offset)} divider {NativeSections.IsDivider(piece)}");
            Mod.Log.Info(text.ToString());
        }

        // The mesh the game draws for a composition, which the mod may have
        // replaced with one of its own variants.
        private void DiagnoseDrawnMesh(string label, Entity composition)
        {
            if (!EntityManager.TryGetComponent<NetCompositionMeshRef>(composition, out var reference))
            {
                Mod.Log.Info($"DIAG layout {label} {composition.Index}: no mesh reference");
                return;
            }
            var text = new System.Text.StringBuilder();
            text.Append($"DIAG layout {label} {composition.Index}: draws mesh {reference.m_Mesh.Index} rotate {reference.m_Rotate}");
            if (reference.m_Mesh != Entity.Null &&
                EntityManager.TryGetComponent<NetCompositionMeshData>(reference.m_Mesh, out var data))
                text.Append($" flags {Flags(data.m_Flags)} width {MedianDiagnostics.N(data.m_Width)} " +
                    $"middleOffset {MedianDiagnostics.N(data.m_MiddleOffset)} " +
                    $"heights {MedianDiagnostics.N(data.m_HeightRange.min)} to {MedianDiagnostics.N(data.m_HeightRange.max)}");
            if (reference.m_Mesh != Entity.Null && EntityManager.HasBuffer<NetCompositionPiece>(reference.m_Mesh))
                foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(reference.m_Mesh, true))
                    text.Append($" | piece {piece.m_Piece.Index} [{piece.m_SectionFlags}] [{piece.m_PieceFlags}] " +
                        $"size{MedianDiagnostics.V(piece.m_Size)} offset{MedianDiagnostics.V(piece.m_Offset)}");
            Mod.Log.Info(text.ToString());
        }

        private void DiagnosePrefab(Entity prefab)
        {
            if (!diagnosedPrefabs.Add(prefab) || !EntityManager.HasBuffer<NetGeometrySection>(prefab)) return;
            using (var list = EntityManager.GetBuffer<NetGeometrySection>(prefab, true).ToNativeArray(Allocator.Temp))
            for (int i = 0; i < list.Length; i++)
            {
                var section = list[i];
                Mod.Log.Info($"DIAG prefab {prefab.Index} section {i}: entity {section.m_Section.Index} [{section.m_Flags}] " +
                    $"all {Flags(section.m_CompositionAll)} any {Flags(section.m_CompositionAny)} none {Flags(section.m_CompositionNone)} " +
                    $"offset{MedianDiagnostics.V(section.m_Offset)}");
                if ((section.m_Flags & NetSectionFlags.Median) == 0) continue;
                DiagnoseSection($"prefab {prefab.Index} section {i}", section.m_Section, 0);
            }
        }

        private void DiagnoseSection(string label, Entity section, int depth)
        {
            if (EntityManager.HasBuffer<NetSectionPiece>(section))
                using (var rules = EntityManager.GetBuffer<NetSectionPiece>(section, true).ToNativeArray(Allocator.Temp))
                foreach (var rule in rules)
                {
                    float width = EntityManager.HasComponent<NetPieceData>(rule.m_Piece)
                        ? EntityManager.GetComponentData<NetPieceData>(rule.m_Piece).m_Width : -1f;
                    Mod.Log.Info($"DIAG {label} rule: piece {rule.m_Piece.Index} [{rule.m_Flags}] width {MedianDiagnostics.N(width)} " +
                        $"all {Flags(rule.m_CompositionAll)} any {Flags(rule.m_CompositionAny)} none {Flags(rule.m_CompositionNone)} " +
                        $"sectionAll [{rule.m_SectionAll}] sectionAny [{rule.m_SectionAny}] sectionNone [{rule.m_SectionNone}] " +
                        $"offset{MedianDiagnostics.V(rule.m_Offset)}");
                }
            if (depth >= 2 || !EntityManager.HasBuffer<NetSubSection>(section)) return;
            using (var subs = EntityManager.GetBuffer<NetSubSection>(section, true).ToNativeArray(Allocator.Temp))
            foreach (var sub in subs)
            {
                Mod.Log.Info($"DIAG {label} subsection: entity {sub.m_SubSection.Index} all {Flags(sub.m_CompositionAll)} " +
                    $"any {Flags(sub.m_CompositionAny)} none {Flags(sub.m_CompositionNone)} sectionAll [{sub.m_SectionAll}] " +
                    $"sectionAny [{sub.m_SectionAny}] sectionNone [{sub.m_SectionNone}]");
                DiagnoseSection($"{label} sub {sub.m_SubSection.Index}", sub.m_SubSection, depth + 1);
            }
        }

        private bool HasClosedComposition(Entity edge, Entity node)
        {
            var composition = EntityManager.GetComponentData<Composition>(edge);
            var ends = EntityManager.GetComponentData<Edge>(edge);
            var data = ends.m_Start == node ? composition.m_StartNode : composition.m_EndNode;
            if (!EntityManager.HasBuffer<NetCompositionPiece>(data) ||
                !EntityManager.HasComponent<NetCompositionData>(data) ||
                (EntityManager.GetComponentData<NetCompositionData>(data).m_Flags.m_General & NativeSections.Closed) == 0)
                return false;
            foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(data, true))
                if (NativeSections.IsDivider(piece)) return true;
            return false;
        }

        private void Refresh()
        {
            expired.Clear();
            using (var entities = placements.ToEntityArray(Allocator.Temp))
            foreach (var node in entities)
            {
                var placement = EntityManager.GetComponentData<ClosedMedian>(node);
                if (!TryLayout(node, ref placement, out var geometry))
                {
                    // A road here caught mid-rebuild (replaced, or split by a road
                    // built onto it): the closure waits for it to settle.
                    if (layoutBuilding && StillRebuilding(node)) continue;
                    string reason = layoutRefusal;
                    if (TryFollowSplit(node, placement, out var followed, out geometry))
                    {
                        Mod.Log.Info($"Closed median at junction {node.Index} follows its road, which was split: " +
                            $"approaches {followed.FirstRoad.Index} and {followed.SecondRoad.Index} " +
                            $"(were {placement.FirstRoad.Index} and {placement.SecondRoad.Index}).");
                        EntityManager.SetComponentData(node, followed);
                        placement = followed;
                        // The game picks the through roads' junction ends again,
                        // now as the closure's: a new segment taken for a side
                        // street meanwhile loses that side street's median end.
                        foreach (var road in new[] { followed.FirstRoad, followed.SecondRoad, node })
                            if (!EntityManager.HasComponent<Updated>(road)) EntityManager.AddComponent<Updated>(road);
                        // And the median is fitted again on the new segment.
                        RefreshJunction(node);
                    }
                    else if (layoutBuilding && StillRebuilding(node)) continue;
                    else
                    {
                        Mod.Log.Info($"Closed median released at junction {node.Index}: its two approaches no longer " +
                            $"share a supported median (built or painted): {reason}" +
                            (followRefusal != null ? $" Following the split road failed: {followRefusal}" : ""));
                        DiagnoseLayout(node, "closure released");
                        expired.Add(node);
                        continue;
                    }
                }
                rebuilding.Remove(node);
                // A closure keeps its kind: one whose roads were replaced so that
                // a built median became paint, or paint a built median, is
                // released, as its turns and drawing were checked for the other.
                if (active.TryGetValue(node, out var previous) && previous.Painted != geometry.Painted)
                {
                    Mod.Log.Info($"Closed median released at junction {node.Index}: its roads changed from a " +
                        $"{(previous.Painted ? "painted" : "built")} to a {(geometry.Painted ? "painted" : "built")} median.");
                    expired.Add(node);
                    continue;
                }
                active[node] = geometry;
                if (geometry.Painted)
                {
                    // Nothing is built for paint: closed once the junction has drained.
                    if (!draining.ContainsKey(node) && confirmed.Add(node))
                    {
                        if (node == lastToggled) status = "Painted median closed.";
                        Mod.Log.Info($"Painted median closed at junction {node.Index}: {geometry.HalfLength * 2f:F1} m across the junction, " +
                            $"{geometry.HalfWidth * 2f:F2} m of paint between the directions.");
                    }
                }
                else if (CanRenderMedian(node) && HasClosedComposition(placement.FirstRoad, node) && HasClosedComposition(placement.SecondRoad, node) &&
                    confirmed.Add(node))
                {
                    if (node == lastToggled) status = "Median closed.";
                    Mod.Log.Info("Native divider compositions active on both approaches of junction " + node.Index);
                }
            }
            foreach (var node in expired) Remove(node);
            expired.Clear();
            foreach (var node in active.Keys)
                if (!EntityManager.HasComponent<ClosedMedian>(node) || EntityManager.HasComponent<Deleted>(node))
                    expired.Add(node);
            foreach (var node in expired)
            {
                active.Remove(node);
                rebuilding.Remove(node);
                confirmed.Remove(node);
                loadRefreshes.Remove(node);
                rechecks.Remove(node);
                pendingRechecks.Remove(node);
                pendingRefreshes.Remove(node);
                rejectedGeometry.Remove(node);
                rejectionReasons.Remove(node);
                draining.Remove(node);
                pendingMedianMeshes.Remove(node);
                pendingRenderMeshes.Remove(node);
            }
        }

        // Frames a closure has waited for a road at its junction to finish
        // being rebuilt. A split or a replacement settles within a frame or
        // two; a closure still waiting after this many is released.
        private readonly Dictionary<Entity, int> rebuilding = new Dictionary<Entity, int>();
        private const int RebuildFrames = 60;

        private bool StillRebuilding(Entity node)
        {
            rebuilding.TryGetValue(node, out int frames);
            if (frames >= RebuildFrames) return false;
            rebuilding[node] = frames + 1;
            return true;
        }

        // A road built onto one of a closure's approaches (a side street or a
        // node further along) splits it: the game deletes that segment and
        // gives the junction a new one in its place. The closure follows when
        // the junction, read as new, still has its through road along every
        // saved approach that is still here, and at least one of them is.
        private bool TryFollowSplit(Entity node, ClosedMedian saved, out ClosedMedian followed, out MedianGeometry geometry)
        {
            followed = default;
            geometry = default;
            followRefusal = null;
            bool firstHere = IsConnected(node, saved.FirstRoad);
            bool secondHere = IsConnected(node, saved.SecondRoad);
            if (firstHere == secondHere) return false;
            Entity kept = firstHere ? saved.FirstRoad : saved.SecondRoad;
            ClosedMedian fresh = default;
            followingSplit = true;
            try
            {
                if (!TryLayout(node, ref fresh, out geometry))
                {
                    followRefusal = layoutRefusal;
                    return false;
                }
            }
            finally { followingSplit = false; }
            if (fresh.FirstRoad != kept && fresh.SecondRoad != kept)
            {
                followRefusal = "The road that stayed is no longer one of the through roads.";
                return false;
            }
            followed = fresh;
            return true;
        }

        private bool IsConnected(Entity node, Entity edge)
        {
            if (edge == Entity.Null || !EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge) ||
                !EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            foreach (var item in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (item.m_Edge == edge) return true;
            return false;
        }

        private void Remove(Entity node)
        {
            rebuilding.Remove(node);
            var placement = EntityManager.GetComponentData<ClosedMedian>(node);
            MedianTextureSystem.Clear(EntityManager, placement.FirstRoad, node);
            MedianTextureSystem.Clear(EntityManager, placement.SecondRoad, node);
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
                using (var edges = EntityManager.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Allocator.Temp))
                foreach (var item in edges) MedianTextureSystem.ClearStrip(EntityManager, item.m_Edge, node);
            EntityManager.RemoveComponent<ClosedMedian>(node);
            active.Remove(node);
            confirmed.Remove(node);
            rechecks.Remove(node);
            pendingRechecks.Remove(node);
            pendingRefreshes.Remove(node);
            rejectedGeometry.Remove(node);
            rejectionReasons.Remove(node);
            draining.Remove(node);
            pendingMedianMeshes.Remove(node);
            pendingRenderMeshes.Remove(node);
            Mark(node, false);
        }

        internal void ResetControls()
        {
            active.Clear();
            rebuilding.Clear();
            draining.Clear();
            confirmed.Clear();
            diagnosedPrefabs.Clear();
            loadRefreshes.Clear();
            rechecks.Clear();
            pendingRechecks.Clear();
            pendingRefreshes.Clear();
            rejectedGeometry.Clear();
            rejectionReasons.Clear();
            pendingMedianMeshes.Clear();
            pendingRenderMeshes.Clear();
            requested = Entity.Null;
            lastToggled = Entity.Null;
            status = "";
        }

        protected override void OnDestroy()
        {
            ResetControls();
            base.OnDestroy();
        }
    }
}




