using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Rendering;
using Game;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    internal struct MedianTextureHalf : IComponentData
    {
        internal Entity Junction;
        internal Entity Composition;
        internal float4 Station;
        internal float4 Fractions;
        internal Entity DedicatedMesh;
        internal float4x4 LeftRail;
        internal float4x4 RightRail;
    }

    internal struct MedianEndTextureHalf : IComponentData
    {
        internal MedianTextureHalf Mapping;
    }

    // Shape rails for the through-lane strip that a side street's junction mesh
    // carries at a closed median. Every side street of a closed junction has
    // one. Rails of zero size, or a mesh other than the dedicated one, draw
    // nothing.
    internal struct MedianStrip : IComponentData
    {
        internal Entity Junction;
        internal Entity Composition;
        internal Entity DedicatedMesh;
        internal float4x4 LeftRail;
        internal float4x4 RightRail;
    }

    internal struct MedianEndStrip : IComponentData
    {
        internal MedianStrip Strip;
    }

    // A closed junction's road end that the renderer found drawn with another
    // mod's copy of its junction mesh.
    internal struct ForeignEnd
    {
        internal Entity Junction;
        internal Entity Edge;
        internal bool Starts;
        internal bool Divider;
    }

    public sealed partial class MedianTextureSystem : GameSystemBase
    {
        private PreCullingSystem culling;
        private BatchManagerSystem batches;
        private AlignMedianSystem alignment;
        private EntityQuery tracked;

        internal static bool TryGetStrip(EntityManager manager, Entity street, bool starts, out MedianStrip strip)
        {
            if (starts) return manager.TryGetComponent(street, out strip);
            bool found = manager.TryGetComponent<MedianEndStrip>(street, out var end);
            strip = end.Strip;
            return found;
        }

        internal static void SetStrip(EntityManager manager, Entity street, bool starts, MedianStrip strip)
        {
            if (starts)
            {
                if (manager.HasComponent<MedianStrip>(street)) manager.SetComponentData(street, strip);
                else manager.AddComponentData(street, strip);
            }
            else
            {
                var end = new MedianEndStrip { Strip = strip };
                if (manager.HasComponent<MedianEndStrip>(street)) manager.SetComponentData(street, end);
                else manager.AddComponentData(street, end);
            }
        }

        internal static void ClearStrip(EntityManager manager, Entity street, Entity node)
        {
            if (manager.TryGetComponent<MedianStrip>(street, out var first) && first.Junction == node)
                manager.RemoveComponent<MedianStrip>(street);
            if (manager.TryGetComponent<MedianEndStrip>(street, out var last) && last.Strip.Junction == node)
                manager.RemoveComponent<MedianEndStrip>(street);
        }

        internal static void Clear(EntityManager manager, Entity road, Entity node)
        {
            if (manager.TryGetComponent<MedianTextureHalf>(road, out var first) && first.Junction == node)
                manager.RemoveComponent<MedianTextureHalf>(road);
            if (manager.TryGetComponent<MedianEndTextureHalf>(road, out var last) && last.Mapping.Junction == node)
                manager.RemoveComponent<MedianEndTextureHalf>(road);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            culling = World.GetOrCreateSystemManaged<PreCullingSystem>();
            batches = World.GetOrCreateSystemManaged<BatchManagerSystem>();
            alignment = World.GetOrCreateSystemManaged<AlignMedianSystem>();
            tracked = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<MedianTextureHalf>(), ComponentType.ReadOnly<MedianEndTextureHalf>(),
                    ComponentType.ReadOnly<MedianStrip>(), ComponentType.ReadOnly<MedianEndStrip>()
                }
            });
            RequireForUpdate(tracked);
        }

        // Road ends that another mod drew with its own junction mesh in the last
        // pass, and for how many passes in a row each has been seen. A copy
        // that stays this long is not a mesh still being swapped.
        private NativeList<ForeignEnd> foreign;
        private JobHandle lastPass;
        private readonly Dictionary<(Entity Edge, bool Starts, bool Divider), int> foreignPasses =
            new Dictionary<(Entity, bool, bool), int>();
        private readonly HashSet<(Entity Edge, bool Starts, bool Divider)> seen =
            new HashSet<(Entity, bool, bool)>();
        private readonly List<(Entity Edge, bool Starts, bool Divider)> gone =
            new List<(Entity, bool, bool)>();
        private readonly HashSet<Entity> known = new HashSet<Entity>();
        private const int ForeignPasses = 30;

        protected override void OnDestroy()
        {
            lastPass.Complete();
            if (foreign.IsCreated) foreign.Dispose();
            base.OnDestroy();
        }

        private void CountForeign()
        {
            lastPass.Complete();
            seen.Clear();
            foreach (var end in foreign)
            {
                var key = (end.Edge, end.Starts, end.Divider);
                if (!seen.Add(key)) continue;
                foreignPasses.TryGetValue(key, out int passes);
                foreignPasses[key] = ++passes;
                if (passes == ForeignPasses && EntityManager.Exists(end.Junction))
                    alignment.ForeignMesh(end.Junction, end.Edge, end.Starts, end.Divider);
            }
            gone.Clear();
            foreach (var key in foreignPasses.Keys) if (!seen.Contains(key)) gone.Add(key);
            foreach (var key in gone) foreignPasses.Remove(key);
            foreign.Clear();
        }

        protected override void OnUpdate()
        {
            if (!foreign.IsCreated) foreign = new NativeList<ForeignEnd>(8, Allocator.Persistent);
            CountForeign();
            var updated = culling.GetUpdatedData(true, out var cullingDependency);
            var groups = batches.GetNativeBatchGroups(true, out var groupDependency);
            var instances = batches.GetNativeBatchInstances(false, out var instanceDependency);
            // The game rewrites every instance near the camera when some render
            // settings change, without marking it updated. The few roads at
            // closed junctions are therefore visited every frame as well.
            var roads = tracked.ToEntityArray(Allocator.TempJob);
            var stripMeshes = new NativeList<Entity>(Allocator.TempJob);
            foreach (var mesh in alignment.StripMeshes) stripMeshes.Add(mesh);
            known.Clear();
            alignment.KnownMeshes(known);
            var knownMeshes = new NativeList<Entity>(known.Count, Allocator.TempJob);
            foreach (var mesh in known) knownMeshes.Add(mesh);
            if (DumpBatches && MedianDiagnostics.Enabled)
            {
                DumpBatches = false;
                JobHandle.CombineDependencies(Dependency,
                    JobHandle.CombineDependencies(cullingDependency, groupDependency, instanceDependency)).Complete();
                DiagnoseBatches(roads, groups, instances.AsParallelInstanceWriter(), stripMeshes);
            }
            var job = new PhaseJob
            {
                Edges = updated,
                Roads = roads,
                StripMeshes = stripMeshes,
                Groups = groups,
                Instances = instances.AsParallelInstanceWriter(),
                Meshes = GetBufferLookup<MeshBatch>(true),
                Nets = GetComponentLookup<Game.Net.Edge>(true),
                Closures = GetComponentLookup<ClosedMedian>(true),
                Starts = GetComponentLookup<MedianTextureHalf>(true),
                Ends = GetComponentLookup<MedianEndTextureHalf>(true),
                StripStarts = GetComponentLookup<MedianStrip>(true),
                StripEnds = GetComponentLookup<MedianEndStrip>(true),
                MeshRefs = GetComponentLookup<NetCompositionMeshRef>(true),
                KnownMeshes = knownMeshes,
                Compositions = GetComponentLookup<Game.Net.Composition>(true),
                Foreign = foreign
            };
            Dependency = job.Schedule(JobHandle.CombineDependencies(Dependency,
                JobHandle.CombineDependencies(cullingDependency, groupDependency, instanceDependency)));
            lastPass = Dependency;
            roads.Dispose(Dependency);
            stripMeshes.Dispose(Dependency);
            knownMeshes.Dispose(Dependency);
            culling.AddCullingDataReader(Dependency);
            batches.AddNativeBatchGroupsReader(Dependency);
            batches.AddNativeBatchInstancesWriter(Dependency);
        }

        // Set when the diagnostics file changes: records which mesh instances
        // each road with a strip or divider mapping draws.
        internal static bool DumpBatches;

        private void DiagnoseBatches(NativeArray<Entity> roads,
            NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> groups,
            NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData>.ParallelInstanceWriter instances,
            NativeList<Entity> stripMeshes)
        {
            foreach (var road in roads)
            {
                if (!EntityManager.HasBuffer<MeshBatch>(road)) continue;
                bool hasStart = EntityManager.TryGetComponent<MedianStrip>(road, out var near);
                bool hasEnd = EntityManager.TryGetComponent<MedianEndStrip>(road, out var far);
                var text = new System.Text.StringBuilder();
                text.Append($"DIAG batches road {road.Index}: start strip {(hasStart ? near.DedicatedMesh.Index : -1)} " +
                    $"rails {(hasStart && !near.LeftRail.Equals(default(float4x4)))} end strip " +
                    $"{(hasEnd ? far.Strip.DedicatedMesh.Index : -1)} rails {(hasEnd && !far.Strip.LeftRail.Equals(default(float4x4)))}");
                foreach (var batch in EntityManager.GetBuffer<MeshBatch>(road, true))
                {
                    text.Append($" | {(NetSubMesh)batch.m_MeshIndex} group {batch.m_GroupIndex} instance {batch.m_InstanceIndex} " +
                        $"meshGroup {batch.m_MeshGroup} tile {batch.m_TileIndex}");
                    if (batch.m_GroupIndex < 0) continue;
                    var group = groups.GetGroupData(batch.m_GroupIndex);
                    bool strip = false;
                    for (int i = 0; i < stripMeshes.Length; i++) strip |= stripMeshes[i] == group.m_Mesh;
                    text.Append($" mesh {group.m_Mesh.Index} {group.m_MeshType} strip {strip}");
                    if (EntityManager.TryGetComponent<NetCompositionMeshData>(group.m_Mesh, out var data))
                        text.Append($" flags {(uint)data.m_Flags.m_General:X8}/{(uint)data.m_Flags.m_Left:X8}/{(uint)data.m_Flags.m_Right:X8}");
                    if (EntityManager.HasBuffer<NetCompositionPiece>(group.m_Mesh))
                    {
                        int preserved = 0;
                        var pieces = EntityManager.GetBuffer<NetCompositionPiece>(group.m_Mesh, true);
                        foreach (var piece in pieces) if ((piece.m_PieceFlags & NetPieceFlags.PreserveShape) != 0) preserved++;
                        text.Append($" pieces {pieces.Length} preserved {preserved}");
                        foreach (var piece in pieces)
                        {
                            if ((piece.m_PieceFlags & NetPieceFlags.PreserveShape) == 0) continue;
                            text.Append($" first shaped piece {piece.m_Piece.Index} [{piece.m_SectionFlags}] [{piece.m_PieceFlags}] " +
                                $"offset{MedianDiagnostics.V(piece.m_Offset)} size{MedianDiagnostics.V(piece.m_Size)}");
                            break;
                        }
                    }
                    if (group.GetPropertyIndex((int)NetProperty.CompositionMatrix6, out int property) && batch.m_InstanceIndex >= 0 &&
                        instances.GetPropertyValue(out float4x4 rail, batch.m_GroupIndex, property, batch.m_InstanceIndex))
                        text.Append($" matrix6 {MedianDiagnostics.M(rail)}");
                }
                if (EntityManager.TryGetComponent<Game.Net.Composition>(road, out var selected))
                    foreach (var (label, composition) in new[] { ("edge", selected.m_Edge), ("start", selected.m_StartNode), ("end", selected.m_EndNode) })
                        if (EntityManager.TryGetComponent<NetCompositionMeshRef>(composition, out var reference))
                            text.Append($" || {label} composition {composition.Index} draws {reference.m_Mesh.Index} rotate {reference.m_Rotate}");
                Mod.Log.Info(text.ToString());
            }
        }

        private struct PhaseJob : IJob
        {
            [ReadOnly] public NativeList<PreCullingData> Edges;
            [ReadOnly] public NativeArray<Entity> Roads;
            [ReadOnly] public NativeList<Entity> StripMeshes;
            [ReadOnly] public NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> Groups;
            public NativeBatchInstances<CullingData, GroupData, BatchData, InstanceData>.ParallelInstanceWriter Instances;
            [ReadOnly] public BufferLookup<MeshBatch> Meshes;
            [ReadOnly] public ComponentLookup<Game.Net.Edge> Nets;
            [ReadOnly] public ComponentLookup<ClosedMedian> Closures;
            [ReadOnly] public ComponentLookup<MedianTextureHalf> Starts;
            [ReadOnly] public ComponentLookup<MedianEndTextureHalf> Ends;
            [ReadOnly] public ComponentLookup<MedianStrip> StripStarts;
            [ReadOnly] public ComponentLookup<MedianEndStrip> StripEnds;
            [ReadOnly] public ComponentLookup<NetCompositionMeshRef> MeshRefs;
            [ReadOnly] public NativeList<Entity> KnownMeshes;
            [ReadOnly] public ComponentLookup<Game.Net.Composition> Compositions;
            public NativeList<ForeignEnd> Foreign;

            public void Execute()
            {
                foreach (var update in Edges) Visit(update.m_Entity);
                foreach (var road in Roads) Visit(road);
            }

            private void Visit(Entity edge)
            {
                bool start = Starts.TryGetComponent(edge, out var near) && Closures.HasComponent(near.Junction);
                bool end = Ends.TryGetComponent(edge, out var far) && Closures.HasComponent(far.Mapping.Junction);
                // A junction mesh that carries strip lanes is shared by every
                // junction that selects its composition, tool previews included.
                // Its lanes are drawn only where a closed junction placed them.
                bool strips = StripMeshes.Length != 0 && Nets.HasComponent(edge);
                if ((!start && !end && !strips) || !Meshes.TryGetBuffer(edge, out var meshes)) return;
                bool stripStart = StripStarts.TryGetComponent(edge, out var nearStrip) &&
                    Closures.HasComponent(nearStrip.Junction);
                bool stripEnd = StripEnds.TryGetComponent(edge, out var farStrip) &&
                    Closures.HasComponent(farStrip.Strip.Junction);
                foreach (var mesh in meshes)
                {
                    var kind = (NetSubMesh)mesh.m_MeshIndex;
                    bool atStart = kind == NetSubMesh.StartNode || kind == NetSubMesh.SubStartNode;
                    bool atEnd = kind == NetSubMesh.EndNode || kind == NetSubMesh.SubEndNode;
                    if (start && atStart) Apply(mesh, near, edge, true);
                    if (end && atEnd) Apply(mesh, far.Mapping, edge, false);
                    if (strips && atStart) ApplyStrip(mesh, stripStart, nearStrip, edge, true);
                    if (strips && atEnd) ApplyStrip(mesh, stripEnd, farStrip.Strip, edge, false);
                }
            }

            private void ApplyStrip(MeshBatch mesh, bool live, MedianStrip strip, Entity edge, bool starts)
            {
                if (mesh.m_GroupIndex < 0 || mesh.m_InstanceIndex < 0) return;
                var group = Groups.GetGroupData(mesh.m_GroupIndex);
                if (group.m_MeshType != MeshType.Net) return;
                if (live && strip.DedicatedMesh != Entity.Null && group.m_Mesh != strip.DedicatedMesh &&
                    Current(edge, starts, strip.Composition) && !IsKnown(group.m_Mesh))
                    Foreign.Add(new ForeignEnd { Junction = strip.Junction, Edge = edge, Starts = starts });
                if (!IsStripMesh(group.m_Mesh)) return;
                bool shown = live && strip.DedicatedMesh == group.m_Mesh;
                WriteRail(mesh, group, NetProperty.CompositionMatrix6, shown ? strip.LeftRail : default);
                WriteRail(mesh, group, NetProperty.CompositionMatrix7, shown ? strip.RightRail : default);
            }

            private void Apply(MeshBatch mesh, MedianTextureHalf mapping, Entity edge, bool starts)
            {
                if (mesh.m_GroupIndex < 0 || mesh.m_InstanceIndex < 0 ||
                    !MeshRefs.TryGetComponent(mapping.Composition, out var meshRef)) return;
                var group = Groups.GetGroupData(mesh.m_GroupIndex);
                if (group.m_MeshType == MeshType.Net && mapping.DedicatedMesh != Entity.Null &&
                    group.m_Mesh != meshRef.m_Mesh && Current(edge, starts, mapping.Composition) && !IsKnown(group.m_Mesh))
                    Foreign.Add(new ForeignEnd { Junction = mapping.Junction, Edge = edge, Starts = starts, Divider = true });
                if (group.m_Mesh != meshRef.m_Mesh || group.m_MeshType != MeshType.Net) return;

                if (mapping.DedicatedMesh == group.m_Mesh)
                {
                    WriteRail(mesh, group, NetProperty.CompositionMatrix6, mapping.LeftRail);
                    WriteRail(mesh, group, NetProperty.CompositionMatrix7, mapping.RightRail);
                }
                // Preserve the existing junction pavement mapping on its own
                // rails. The independent median uses only matrices 6 and 7.
                Write(mesh, group, NetProperty.CompositionMatrix4, mapping.Station);
                WriteInner(mesh, group, NetProperty.CompositionMatrix0, NetProperty.CompositionMatrix2,
                    mapping.Station, mapping.Fractions.xz);
                WriteInner(mesh, group, NetProperty.CompositionMatrix3, NetProperty.CompositionMatrix1,
                    mapping.Station, mapping.Fractions.yw);
            }

            private bool IsStripMesh(Entity mesh)
            {
                for (int i = 0; i < StripMeshes.Length; i++) if (StripMeshes[i] == mesh) return true;
                return false;
            }

            // Whether a mapping still names the composition the road end selects.
            // A junction edited since its last fit draws a new composition's mesh
            // until it is fitted again.
            private bool Current(Entity edge, bool starts, Entity composition) =>
                Compositions.TryGetComponent(edge, out var selected) &&
                (starts ? selected.m_StartNode : selected.m_EndNode) == composition;

            // A mesh this mod made or replaced. A junction instance can still
            // draw one of these for a frame or two after a composition changed.
            private bool IsKnown(Entity mesh)
            {
                for (int i = 0; i < KnownMeshes.Length; i++) if (KnownMeshes[i] == mesh) return true;
                return false;
            }

            // Writes only what changed, so a road visited every frame costs a read.
            private void WriteRail(MeshBatch mesh, GroupData group, NetProperty property, float4x4 rail)
            {
                if (!group.GetPropertyIndex((int)property, out int index)) return;
                if (Instances.GetPropertyValue(out float4x4 current, mesh.m_GroupIndex, index, mesh.m_InstanceIndex) &&
                    Same(current, rail)) return;
                Instances.SetPropertyValue(rail, mesh.m_GroupIndex, index, mesh.m_InstanceIndex);
            }

            private static bool Same(float4x4 a, float4x4 b) =>
                math.all(a.c0 == b.c0) && math.all(a.c1 == b.c1) && math.all(a.c2 == b.c2) && math.all(a.c3 == b.c3);

            private void WriteInner(MeshBatch mesh, GroupData group, NetProperty outer, NetProperty inner,
                float4 station, float2 fractions)
            {
                if (!group.GetPropertyIndex((int)outer, out int index) ||
                    !Instances.GetPropertyValue(out float4x4 matrix, mesh.m_GroupIndex, index, mesh.m_InstanceIndex)) return;
                float4 outerStation = new float4(matrix.c0.w, matrix.c1.w, matrix.c2.w, matrix.c3.w);
                // Account for the outer rail at both ends of the curb profile.
                float4 weights = NativeCurbFit.WeightControls(fractions.x, fractions.y);
                Write(mesh, group, inner, MedianTexturePhase.Compensate(station, outerStation, weights));
            }

            private void Write(MeshBatch mesh, GroupData group, NetProperty property, float4 station)
            {
                if (!group.GetPropertyIndex((int)property, out int index) ||
                    !Instances.GetPropertyValue(out float4x4 matrix, mesh.m_GroupIndex, index, mesh.m_InstanceIndex)) return;
                if (math.all(new float4(matrix.c0.w, matrix.c1.w, matrix.c2.w, matrix.c3.w) == station)) return;
                matrix.c0.w = station.x;
                matrix.c1.w = station.y;
                matrix.c2.w = station.z;
                matrix.c3.w = station.w;
                Instances.SetPropertyValue(matrix, mesh.m_GroupIndex, index, mesh.m_InstanceIndex);
            }
        }
    }
}
