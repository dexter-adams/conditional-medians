using System.Collections.Generic;
using System.Text;
using Colossal.Entities;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using Curve = Game.Net.Curve;
using SubLane = Game.Net.SubLane;

namespace ConditionalMedians2
{
    // Read-only records of the traffic at closed junctions, written to the mod
    // log while diagnostics.txt exists: when the file changes and then every 15
    // seconds, at most a few times per session. For each closed junction it lists
    // the car connectors that cross the median's box, and for each car near the
    // median its lane, its speed and the start of its path. It changes nothing.
    public sealed partial class VehicleDiagnostics : GameSystemBase
    {
        internal static bool Dump;
        private const int MaximumDumps = 8;
        private const float Margin = 6f;
        private MedianSystem medians;
        private PrefabSystem prefabs;
        private MedianLaneBlockSystem blocks;
        private MedianPathScanSystem scan;
        private PathfindQueueSystem queue;
        private EntityQuery cars;
        private EntityQuery markings;
        private EntityQuery walks;
        private float nextDump;
        // The markings are listed only when the diagnostics file changes.
        private bool dumpMarkings;
        private int dumps;

        protected override void OnCreate()
        {
            base.OnCreate();
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
            prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            blocks = World.GetOrCreateSystemManaged<MedianLaneBlockSystem>();
            scan = World.GetOrCreateSystemManaged<MedianPathScanSystem>();
            queue = World.GetOrCreateSystemManaged<PathfindQueueSystem>();
            cars = GetEntityQuery(ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<Game.Objects.Transform>(),
                ComponentType.Exclude<Deleted>());
            markings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Game.Net.LaneGeometry>() },
                None = new[] { ComponentType.ReadOnly<CarLane>(), ComponentType.ReadOnly<Game.Net.PedestrianLane>(),
                    ComponentType.ReadOnly<Game.Net.TrackLane>(), ComponentType.ReadOnly<Game.Net.ParkingLane>(),
                    ComponentType.ReadOnly<Deleted>() }
            });
            walks = GetEntityQuery(ComponentType.ReadOnly<Game.Net.PedestrianLane>(), ComponentType.ReadOnly<Curve>(),
                ComponentType.Exclude<Deleted>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            dumps = 0;
        }

        protected override void OnUpdate()
        {
            if (!MedianDiagnostics.Enabled || dumps >= MaximumDumps) return;
            float now = UnityEngine.Time.unscaledTime;
            if (!Dump && now < nextDump) return;
            dumpMarkings = Dump;
            Dump = false;
            nextDump = now + 15f;
            dumps++;
            foreach (var pair in medians.ActiveJunctions) DumpJunction(pair.Key, pair.Value);
        }

        private void DumpJunction(Entity node, MedianGeometry median)
        {
            var text = new StringBuilder();
            text.Append($"DIAG traffic junction {node.Index}: median half length {median.HalfLength:F2} half width {median.HalfWidth:F2}");
            if (EntityManager.HasBuffer<SubLane>(node))
            {
                int connectors = 0;
                foreach (var item in EntityManager.GetBuffer<SubLane>(node, true))
                {
                    Entity lane = item.m_SubLane;
                    if (!EntityManager.TryGetComponent<CarLane>(lane, out var car) ||
                        !EntityManager.TryGetComponent<Curve>(lane, out var curve)) continue;
                    connectors++;
                    if (!median.Hits(curve.m_Bezier)) continue;
                    text.Append($" | crossing connector {lane.Index} [{car.m_Flags}] from {Local(median, curve.m_Bezier.a)} " +
                        $"to {Local(median, curve.m_Bezier.d)}{(EntityManager.HasComponent<Game.Net.MasterLane>(lane) ? " master" : "")}");
                }
                text.Append($" | {connectors} car connectors in all");
            }
            // The lanes cut from the route graph, read back from the graph itself.
            var cut = blocks.GraphLanes(node);
            text.Append($" | {cut.Count} cut:");
            var data = queue.GetDataContainer(out Unity.Jobs.JobHandle reading);
            reading.Complete();
            foreach (var lane in cut)
                text.Append(data.GetEdge(lane, out var edge) ? $" {lane.Index} [{data.GetFlags(edge)}]" : $" {lane.Index} [no edge]");
            queue.AddDataReader(default(Unity.Jobs.JobHandle));
            text.Append($" | scan rerouted {scan.Rerouted} committed sightings {scan.Committed}");
            Mod.Log.Info(text.ToString());

            using (var entities = cars.ToEntityArray(Allocator.Temp))
            using (var transforms = cars.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp))
                for (int i = 0; i < entities.Length; i++)
                    if (median.Near(transforms[i].m_Position, Margin))
                        DumpCar(node, median, entities[i], transforms[i]);
            if (dumpMarkings) DumpMarkings(node, median);
        }

        // Every drawn lane without traffic near the junction: road markings,
        // the game's and other mods' alike, with what draws and owns each.
        private void DumpMarkings(Entity node, MedianGeometry median)
        {
            using (var entities = markings.ToEntityArray(Allocator.Temp))
            foreach (var lane in entities)
            {
                var curve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
                if (!median.Near(curve.a, 30f) && !median.Near(curve.d, 30f)) continue;
                string name = EntityManager.TryGetComponent<PrefabRef>(lane, out var prefab) &&
                    prefabs.TryGetPrefab(prefab, out PrefabBase asset) ? asset.name : "?";
                string owner = EntityManager.TryGetComponent<Owner>(lane, out var parent)
                    ? (EntityManager.HasComponent<Game.Net.Node>(parent.m_Owner) ? "node " : EntityManager.HasComponent<Game.Net.Edge>(parent.m_Owner) ? "edge " : "other ") + parent.m_Owner.Index
                    : "none";
                var text = new StringBuilder();
                text.Append($"DIAG marking {lane.Index} ({name}) at junction {node.Index}: owner {owner} from {Local(median, curve.a)} " +
                    $"to {Local(median, curve.d)} length {Colossal.Mathematics.MathUtils.Length(curve):F1}");
                if (prefab.m_Prefab != Entity.Null && EntityManager.TryGetComponent<NetLaneGeometryData>(prefab.m_Prefab, out var size))
                    text.Append($" size {size.m_Size}");
                text.Append(" | types:");
                using (var types = EntityManager.GetComponentTypes(lane, Allocator.Temp))
                    foreach (var type in types) text.Append(' ').Append(type.GetManagedType()?.Name ?? type.ToString());
                Mod.Log.Info(text.ToString());
            }
            // Pedestrian crossings near the junction, which cut the median lines where drawn.
            using (var entities = walks.ToEntityArray(Allocator.Temp))
            foreach (var lane in entities)
            {
                var walk = EntityManager.GetComponentData<Game.Net.PedestrianLane>(lane);
                if ((walk.m_Flags & (Game.Net.PedestrianLaneFlags.Crosswalk | Game.Net.PedestrianLaneFlags.Unsafe)) == 0) continue;
                var curve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
                if (!median.Near(curve.a, 30f) && !median.Near(curve.d, 30f)) continue;
                string name = EntityManager.TryGetComponent<PrefabRef>(lane, out var prefab) &&
                    prefabs.TryGetPrefab(prefab, out PrefabBase asset) ? asset.name : "?";
                string owner = EntityManager.TryGetComponent<Owner>(lane, out var parent)
                    ? (EntityManager.HasComponent<Game.Net.Node>(parent.m_Owner) ? "node " : EntityManager.HasComponent<Game.Net.Edge>(parent.m_Owner) ? "edge " : "other ") + parent.m_Owner.Index
                    : "none";
                var text = new StringBuilder();
                text.Append($"DIAG crossing {lane.Index} ({name}) [{walk.m_Flags}] at junction {node.Index}: owner {owner} from {Local(median, curve.a)} " +
                    $"to {Local(median, curve.d)} length {Colossal.Mathematics.MathUtils.Length(curve):F1}");
                if (prefab.m_Prefab != Entity.Null && EntityManager.TryGetComponent<NetLaneGeometryData>(prefab.m_Prefab, out var size))
                    text.Append($" size {size.m_Size}");
                if (prefab.m_Prefab != Entity.Null && EntityManager.TryGetComponent<NetLaneData>(prefab.m_Prefab, out var data))
                    text.Append($" width {data.m_Width:F2}");
                text.Append(" | types:");
                using (var types = EntityManager.GetComponentTypes(lane, Allocator.Temp))
                    foreach (var type in types) text.Append(' ').Append(type.GetManagedType()?.Name ?? type.ToString());
                Mod.Log.Info(text.ToString());
            }
        }

        private void DumpCar(Entity node, MedianGeometry median, Entity vehicle, Game.Objects.Transform transform)
        {
            var text = new StringBuilder();
            string name = EntityManager.TryGetComponent<PrefabRef>(vehicle, out var prefab) &&
                prefabs.TryGetPrefab(prefab, out PrefabBase asset) ? asset.name : "?";
            float speed = EntityManager.TryGetComponent<Game.Objects.Moving>(vehicle, out var moving)
                ? math.length(moving.m_Velocity) : -1f;
            text.Append($"DIAG traffic car {vehicle.Index} ({name}) at junction {node.Index}: local {Local(median, transform.m_Position)} " +
                $"speed {speed:F2} in box {median.Hits(transform.m_Position, transform.m_Position)}");
            if (EntityManager.TryGetComponent<CarCurrentLane>(vehicle, out var current))
                text.Append($" | lane {Lane(median, current.m_Lane)} at {current.m_CurvePosition.x:F2}->{current.m_CurvePosition.z:F2} " +
                    $"flags [{current.m_LaneFlags}]{(current.m_ChangeLane != Entity.Null ? " changing to " + Lane(median, current.m_ChangeLane) : "")}");
            if (EntityManager.TryGetComponent<PathOwner>(vehicle, out var owner))
                text.Append($" | path state [{owner.m_State}] element {owner.m_ElementIndex}");
            if (EntityManager.HasBuffer<PathElement>(vehicle))
            {
                var path = EntityManager.GetBuffer<PathElement>(vehicle, true);
                int start = owner.m_ElementIndex;
                text.Append($" of {path.Length}:");
                for (int i = start; i < path.Length && i < start + 6; i++)
                    text.Append($" {Lane(median, path[i].m_Target)};");
            }
            Mod.Log.Info(text.ToString());
        }

        private string Lane(MedianGeometry median, Entity lane)
        {
            if (lane == Entity.Null) return "none";
            var text = new StringBuilder();
            text.Append(lane.Index);
            if (EntityManager.TryGetComponent<Owner>(lane, out var owner))
                text.Append($" of {(EntityManager.HasComponent<Game.Net.Node>(owner.m_Owner) ? "node" : EntityManager.HasComponent<Game.Net.Edge>(owner.m_Owner) ? "edge" : "object")} {owner.m_Owner.Index}");
            if (EntityManager.TryGetComponent<CarLane>(lane, out var car)) text.Append($" [{car.m_Flags}]");
            if (EntityManager.TryGetComponent<Curve>(lane, out var curve) && median.Hits(curve.m_Bezier)) text.Append(" crosses");
            return text.ToString();
        }

        private static string Local(MedianGeometry median, float3 point)
        {
            var local = median.Local(point);
            return $"({local.x:F1},{local.y:F1},{local.z:F1})";
        }
    }
}
