using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    internal sealed class MedianRenderMesh
    {
        private readonly EntityManager entities;
        private readonly Dictionary<Entity, Variant> variants = new Dictionary<Entity, Variant>();
        private readonly Dictionary<Entity, Layout> layouts = new Dictionary<Entity, Layout>();
        private readonly List<Entity> pending = new List<Entity>();
        // Node compositions drawn with the game's native deformation, and the
        // closed junctions that cannot use the dedicated mesh themselves, with
        // the compositions of their two halves.
        private readonly HashSet<Entity> nativeOnly = new HashSet<Entity>();
        private readonly Dictionary<Entity, (Entity First, Entity Second)> nativeRequests =
            new Dictionary<Entity, (Entity, Entity)>();
        private readonly Dictionary<(Entity Source, uint Signature), StripVariant> strips =
            new Dictionary<(Entity, uint), StripVariant>();
        private readonly Dictionary<Entity, Entity> stripSources = new Dictionary<Entity, Entity>();
        private readonly List<(Entity Source, uint Signature)> pendingStrips = new List<(Entity, uint)>();
        private readonly Dictionary<Entity, (float3[] Points, int[] Triangles)> pieceMeshes =
            new Dictionary<Entity, (float3[], int[])>();
        private readonly List<float> centers = new List<float>();
        private readonly List<float3[]> pieceVertices = new List<float3[]>();
        private readonly List<int[]> pieceTriangles = new List<int[]>();
        private readonly EntityQuery startStrips;
        private readonly EntityQuery endStrips;

        internal MedianRenderMesh(EntityManager entities)
        {
            this.entities = entities;
            startStrips = entities.CreateEntityQuery(ComponentType.ReadOnly<MedianStrip>());
            endStrips = entities.CreateEntityQuery(ComponentType.ReadOnly<MedianEndStrip>());
        }

        internal bool Ready(Entity composition) =>
            entities.TryGetComponent<NetCompositionMeshRef>(composition, out var reference) &&
            reference.m_Mesh != Entity.Null;

        internal bool Describe(Entity composition, Entity divider, out Layout layout)
        {
            layout = default;
            if (!entities.TryGetComponent<NetCompositionMeshRef>(composition, out var reference) ||
                reference.m_Rotate || !entities.TryGetComponent<NetCompositionMeshData>(reference.m_Mesh, out var data))
                return false;
            if (layouts.TryGetValue(reference.m_Mesh, out layout)) return layout.Divider == divider;
            var pieces = entities.GetBuffer<NetCompositionPiece>(reference.m_Mesh, true);
            int index = -1;
            for (int i = 0; i < pieces.Length; i++)
            {
                var piece = pieces[i];
                if ((piece.m_PieceFlags & NetPieceFlags.PreserveShape) != 0)
                {
                    Mod.Log.Info($"Median render layout uses shape rails for another piece {piece.m_Piece.Index}.");
                    return false;
                }
                if (!NativeSections.IsDivider(piece)) continue;
                if (index != -1 || piece.m_Piece != divider ||
                    (piece.m_SectionFlags & (NetSectionFlags.FlipMesh | NetSectionFlags.HalfLength)) != 0)
                {
                    Mod.Log.Info($"Median render layout divider needs a different fit: {piece.m_Piece.Index}, {divider.Index}, {piece.m_SectionFlags}.");
                    return false;
                }
                index = i;
            }
            if (index < 0) return false;
            var median = pieces[index];
            float endZ = median.m_Offset.z;
            float length = median.m_Size.z * 0.5f;
            if (math.abs(endZ) > 0.001f || length < 0.1f ||
                (length > data.m_Width && !HasUniformProfile(median.m_Piece, length)))
            {
                Mod.Log.Info($"Median render layout has a nonstandard source extent: {endZ}, {length}, {data.m_Width}.");
                return false;
            }
            layout = new Layout(reference.m_Mesh, median.m_Piece, index,
                data.m_Width, median.m_Offset.x, math.min(length, data.m_Width), composition);
            layouts[reference.m_Mesh] = layout;
            return true;
        }

        private bool HasUniformProfile(Entity piece, float halfLength)
        {
            // Native shape coordinates clamp at the road width. A longer tile
            // can use that interval when its cross-section is an extrusion.
            var vertices = entities.GetBuffer<MeshVertex>(piece, true);
            var inner = new HashSet<int2>();
            var outer = new HashSet<int2>();
            const float tolerance = 0.012f;
            foreach (var vertex in vertices)
            {
                var p = vertex.m_Vertex;
                var section = (int2)math.round(p.xy * 1000f);
                if (math.abs(p.z) <= tolerance) inner.Add(section);
                if (math.abs(p.z - halfLength) <= tolerance) outer.Add(section);
            }
            if (inner.Count == 0 || !inner.SetEquals(outer)) return false;
            foreach (var vertex in vertices)
            {
                var p = vertex.m_Vertex;
                if (p.z >= -tolerance && !inner.Contains((int2)math.round(p.xy * 1000f))) return false;
            }
            return true;
        }

        internal bool TryGetMesh(Layout layout, out Entity mesh)
        {
            if (!variants.TryGetValue(layout.Source, out var variant))
            {
                variant = new Variant { Layout = layout };
                variants.Add(layout.Source, variant);
                pending.Add(layout.Source);
            }
            mesh = variant.Mesh;
            return mesh != Entity.Null;
        }

        // The vertex lattice of a strip's top lanes in the side street's junction
        // mesh, read from the lane pieces' own vertices and triangles. Pending
        // while the composition has no mesh yet: the game makes it at the end
        // of the frame in which it selected a new composition. "reason" says
        // why a mesh cannot carry the strip.
        internal StripState StripLattice(Entity composition, List<StripPiece> lanes, float band,
            NodeSurface.Lattice lattice, out float width, out float halfLength, out string reason)
        {
            width = 0f;
            halfLength = 0f;
            reason = null;
            lattice.Clear();
            if (lanes.Count == 0) return Refuse("it has no lanes beside the median", out reason);
            if (!entities.TryGetComponent<NetCompositionMeshRef>(composition, out var reference) ||
                reference.m_Mesh == Entity.Null) return StripState.Pending;
            if (reference.m_Rotate) return Refuse("the side street's junction mesh is turned", out reason);
            Entity source = Source(reference.m_Mesh);
            if (!entities.TryGetComponent<NetCompositionMeshData>(source, out var data) ||
                !entities.HasBuffer<NetCompositionPiece>(source))
                return Refuse("the side street's junction mesh has no pieces", out reason);
            // The strip needs the shape rails for itself.
            foreach (var piece in entities.GetBuffer<NetCompositionPiece>(source, true))
                if ((piece.m_PieceFlags & NetPieceFlags.PreserveShape) != 0)
                    return Refuse("the side street's junction mesh uses its shape rails for another piece", out reason);
            float length = lanes[0].Size.z * 0.5f;
            if (length < 0.1f) return Refuse("the lane pieces have no length", out reason);
            centers.Clear();
            pieceVertices.Clear();
            pieceTriangles.Clear();
            foreach (var lane in lanes)
            {
                if (math.abs(lane.Size.z * 0.5f - length) > 0.01f)
                    return Refuse("the lane pieces differ in length", out reason);
                if (!TryGetPieceMesh(lane.Piece, out var points, out var triangles))
                    return Refuse("a lane piece has no mesh data", out reason);
                if (length > data.m_Width && !HasUniformProfile(lane.Piece, length))
                    return Refuse("a lane piece changes along its length", out reason);
                centers.Add(lane.Center);
                pieceVertices.Add(points);
                pieceTriangles.Add(triangles);
            }
            width = data.m_Width;
            halfLength = math.min(length, data.m_Width);
            if (!lattice.Build(band, width, length, centers, pieceVertices, pieceTriangles))
                return Refuse("the lane pieces have no rows of vertices across the junction", out reason);
            return StripState.Ready;
        }

        private static StripState Refuse(string why, out string reason)
        {
            reason = why;
            return StripState.Unavailable;
        }

        private bool TryGetPieceMesh(Entity piece, out float3[] points, out int[] triangles)
        {
            if (pieceMeshes.TryGetValue(piece, out var cached))
            {
                points = cached.Points;
                triangles = cached.Triangles;
                return true;
            }
            points = null;
            triangles = null;
            if (!entities.HasBuffer<MeshVertex>(piece) || !entities.HasBuffer<MeshIndex>(piece)) return false;
            var vertices = entities.GetBuffer<MeshVertex>(piece, true);
            var indices = entities.GetBuffer<MeshIndex>(piece, true);
            points = new float3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) points[i] = vertices[i].m_Vertex;
            // The game reads a piece's triangles material by material.
            var list = new List<int>(indices.Length);
            if (entities.HasBuffer<MeshMaterial>(piece))
            {
                foreach (var material in entities.GetBuffer<MeshMaterial>(piece, true))
                    for (int i = material.m_StartIndex; i + 2 < material.m_StartIndex + material.m_IndexCount &&
                        i + 2 < indices.Length; i += 3)
                    {
                        list.Add(indices[i].m_Index);
                        list.Add(indices[i + 1].m_Index);
                        list.Add(indices[i + 2].m_Index);
                    }
            }
            else
                for (int i = 0; i < indices.Length; i++) list.Add(indices[i].m_Index);
            triangles = list.ToArray();
            pieceMeshes[piece] = (points, triangles);
            return true;
        }

        // A side street's junction mesh can carry the through road's lanes as
        // extra pieces on the shape rails. Those lanes then run across the
        // junction as one strip with the through road's own surface direction,
        // above the side street's fanned pavement. StripLattice must have
        // accepted the composition and its lanes.
        internal StripState TryGetStrip(Entity composition, uint signature, List<StripPiece> lanes, float width,
            float halfLength, Entity edge, bool starts, out Entity mesh)
        {
            mesh = Entity.Null;
            if (!entities.TryGetComponent<NetCompositionMeshRef>(composition, out var reference) ||
                reference.m_Mesh == Entity.Null) return StripState.Pending;
            // There is one strip mesh for each source mesh and set of lanes, and
            // compositions that share a source share it. A composition draws one
            // mesh for every junction that selects it, so a junction with other
            // lanes or another skirt waits while another junction draws a strip
            // through the same composition. Meshes are never replaced: two sets
            // of lanes that replaced each other's mesh before either was drawn
            // made new meshes without end.
            var key = (Source(reference.m_Mesh), signature);
            strips.TryGetValue(key, out var variant);
            if (Conflicts(composition, variant?.Mesh ?? Entity.Null, edge, starts)) return StripState.Unavailable;
            if (variant == null)
            {
                variant = new StripVariant
                {
                    Composition = composition, Signature = signature, Lanes = lanes.ToArray(),
                    Width = width, HalfLength = halfLength
                };
                strips.Add(key, variant);
                pendingStrips.Add(key);
                return StripState.Pending;
            }
            mesh = variant.Mesh;
            return mesh != Entity.Null ? StripState.Ready : StripState.Pending;
        }

        // Whether TryGetStrip still refuses this side-street end because another
        // junction draws another strip through the same composition.
        internal bool Blocks(Entity composition, uint signature, Entity edge, bool starts)
        {
            if (!entities.TryGetComponent<NetCompositionMeshRef>(composition, out var reference) ||
                reference.m_Mesh == Entity.Null) return false;
            strips.TryGetValue((Source(reference.m_Mesh), signature), out var variant);
            return Conflicts(composition, variant?.Mesh ?? Entity.Null, edge, starts);
        }

        // Whether a junction other than the one at this side-street end draws a
        // strip through the composition with another mesh than this one.
        private bool Conflicts(Entity composition, Entity mesh, Entity edge, bool starts)
        {
            using (var streets = startStrips.ToEntityArray(Allocator.Temp))
            using (var mappings = startStrips.ToComponentDataArray<MedianStrip>(Allocator.Temp))
                for (int i = 0; i < streets.Length; i++)
                    if ((streets[i] != edge || !starts) && DrawsOther(mappings[i], composition, mesh)) return true;
            using (var streets = endStrips.ToEntityArray(Allocator.Temp))
            using (var mappings = endStrips.ToComponentDataArray<MedianEndStrip>(Allocator.Temp))
                for (int i = 0; i < streets.Length; i++)
                    if ((streets[i] != edge || starts) && DrawsOther(mappings[i].Strip, composition, mesh)) return true;
            return false;
        }

        private bool DrawsOther(MedianStrip strip, Entity composition, Entity mesh) =>
            strip.Composition == composition && strip.DedicatedMesh != Entity.Null && strip.DedicatedMesh != mesh &&
            !strip.LeftRail.Equals(default(float4x4)) && entities.HasComponent<ClosedMedian>(strip.Junction);

        private Entity Source(Entity mesh) => stripSources.TryGetValue(mesh, out var known) ? known : mesh;

        // Every mesh made to carry a strip, for every source mesh and set of lanes.
        internal Dictionary<Entity, Entity>.KeyCollection StripMeshes => stripSources.Keys;

        internal void Prepare()
        {
            foreach (var source in pendingStrips)
            {
                var variant = strips[source];
                if (variant.Template != Entity.Null || !entities.Exists(variant.Composition) ||
                    !entities.HasBuffer<NetCompositionPiece>(source.Source)) continue;
                Entity template = entities.CreateEntity(typeof(NetCompositionData), typeof(NetCompositionPiece),
                    typeof(NetCompositionMeshRef), typeof(Created));
                entities.SetComponentData(template, entities.GetComponentData<NetCompositionData>(variant.Composition));
                var pieces = entities.GetBuffer<NetCompositionPiece>(template);
                pieces.CopyFrom(entities.GetBuffer<NetCompositionPiece>(source.Source, true));
                int section = pieces.Length;
                foreach (var lane in variant.Lanes)
                {
                    pieces.Add(new NetCompositionPiece
                    {
                        m_Piece = lane.Piece, m_Offset = new float3(lane.Center, -lane.Drop, 0f), m_Size = lane.Size,
                        m_SectionFlags = NetSectionFlags.HalfLength,
                        m_PieceFlags = lane.Flags | NetPieceFlags.PreserveShape, m_SectionIndex = section
                    });
                }
                variant.Template = template;
            }
            foreach (var source in pending)
            {
                var variant = variants[source];
                if (variant.Template != Entity.Null || !entities.Exists(variant.Layout.Composition) ||
                    !entities.HasBuffer<NetCompositionPiece>(source)) continue;
                // Submit a render-only template between the gameplay composition
                // system and the game's mesh-ref factory. The factory owns the
                // render mesh, materials, batch groups and LOD lifecycle.
                Entity template = entities.CreateEntity(typeof(NetCompositionData), typeof(NetCompositionPiece),
                    typeof(NetCompositionMeshRef), typeof(Created));
                entities.SetComponentData(template, entities.GetComponentData<NetCompositionData>(variant.Layout.Composition));
                var pieces = entities.GetBuffer<NetCompositionPiece>(template);
                pieces.CopyFrom(entities.GetBuffer<NetCompositionPiece>(source, true));
                var median = pieces[variant.Layout.PieceIndex];
                median.m_PieceFlags |= NetPieceFlags.PreserveShape;
                median.m_SectionFlags |= NetSectionFlags.HalfLength;
                pieces[variant.Layout.PieceIndex] = median;
                variant.Template = template;
            }
        }

        internal void Collect()
        {
            for (int i = pendingStrips.Count - 1; i >= 0; i--)
            {
                var variant = strips[pendingStrips[i]];
                if (variant.Template == Entity.Null ||
                    !entities.TryGetComponent<NetCompositionMeshRef>(variant.Template, out var reference) ||
                    reference.m_Mesh == Entity.Null) continue;
                entities.DestroyEntity(variant.Template);
                variant.Template = Entity.Null;
                variant.Mesh = reference.m_Mesh;
                stripSources[variant.Mesh] = pendingStrips[i].Source;
                pendingStrips.RemoveAt(i);
                Mod.Log.Info($"Through-lane strip prepared for render mesh {variant.Mesh.Index}.");
            }
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var variant = variants[pending[i]];
                if (variant.Template == Entity.Null ||
                    !entities.TryGetComponent<NetCompositionMeshRef>(variant.Template, out var reference) ||
                    reference.m_Mesh == Entity.Null) continue;
                entities.DestroyEntity(variant.Template);
                variant.Template = Entity.Null;
                variant.Mesh = reference.m_Mesh;
                layouts[variant.Mesh] = variant.Layout;
                pending.RemoveAt(i);
                Mod.Log.Info($"Independent median texture prepared for render mesh {variant.Mesh.Index}.");
            }
        }

        internal void Assign(Entity composition, Entity mesh)
        {
            var current = entities.GetComponentData<NetCompositionMeshRef>(composition);
            if (current.m_Mesh == mesh) return;
            if (!originals.ContainsKey(composition) && !IsModMesh(current.m_Mesh))
            {
                originals[composition] = current;
                if (MedianDiagnostics.Enabled)
                    Mod.Log.Info($"DIAG composition {composition.Index} drew mesh {current.m_Mesh.Index} " +
                        $"rotate {current.m_Rotate} before mesh {mesh.Index}");
            }
            entities.SetComponentData(composition, new NetCompositionMeshRef { m_Mesh = mesh });
        }

        // The game's own mesh references of compositions that draw a mesh of this mod.
        private readonly Dictionary<Entity, NetCompositionMeshRef> originals = new Dictionary<Entity, NetCompositionMeshRef>();

        private bool IsModMesh(Entity mesh) =>
            stripSources.ContainsKey(mesh) || (layouts.TryGetValue(mesh, out var layout) && layout.Source != mesh);

        // Gives a composition the mesh reference the game had given it.
        internal void Restore(Entity composition)
        {
            if (originals.TryGetValue(composition, out var original) && entities.Exists(original.m_Mesh) &&
                !entities.GetComponentData<NetCompositionMeshRef>(composition).Equals(original))
                entities.SetComponentData(composition, original);
        }

        // A junction whose divider cannot be drawn on its own rails. Its two
        // compositions are drawn natively at once; the junctions that share
        // them follow when the native set is next worked out.
        internal void RequestNative(Entity node, Entity first, Entity second)
        {
            nativeRequests[node] = (first, second);
            UseNative(first);
            UseNative(second);
        }

        // A junction that shares a native composition draws both of its halves
        // natively at once, without a request of its own.
        internal void FollowNative(Entity first, Entity second)
        {
            UseNative(first);
            UseNative(second);
        }

        // Taken back before each fit of the junction, which asks again if it must.
        internal void ReleaseNative(Entity node) => nativeRequests.Remove(node);

        internal bool HasNative => nativeRequests.Count != 0 || nativeOnly.Count != 0;

        internal Dictionary<Entity, (Entity First, Entity Second)> NativeRequests => nativeRequests;

        internal void CollectNative(HashSet<Entity> compositions) => compositions.UnionWith(nativeOnly);

        internal void CollectRequested(HashSet<Entity> compositions)
        {
            foreach (var request in nativeRequests.Values)
            {
                compositions.Add(request.First);
                compositions.Add(request.Second);
            }
        }

        // Makes the given compositions the native set. A composition that leaves
        // it gets its dedicated mesh back when a junction using it is fitted.
        internal void SetNative(HashSet<Entity> compositions)
        {
            nativeOnly.Clear();
            foreach (var composition in compositions) UseNative(composition);
        }

        private void UseNative(Entity composition)
        {
            // A render reference is shared by nodes with the same composition.
            // Keep it compatible while any instance needs the native deformation.
            nativeOnly.Add(composition);
            if (entities.TryGetComponent<NetCompositionMeshRef>(composition, out var reference) &&
                layouts.TryGetValue(reference.m_Mesh, out var layout) && layout.Source != reference.m_Mesh &&
                entities.Exists(layout.Source)) Assign(composition, layout.Source);
        }

        internal bool IsNativeOnly(Entity composition) => nativeOnly.Contains(composition);

        internal void ResetPending()
        {
            foreach (var source in pendingStrips)
            {
                var variant = strips[source];
                if (variant.Template != Entity.Null && entities.Exists(variant.Template))
                    entities.DestroyEntity(variant.Template);
                strips.Remove(source);
            }
            pendingStrips.Clear();
            nativeOnly.Clear();
            nativeRequests.Clear();
            foreach (var source in pending)
            {
                var variant = variants[source];
                if (variant.Template != Entity.Null && entities.Exists(variant.Template))
                    entities.DestroyEntity(variant.Template);
                variants.Remove(source);
            }
            pending.Clear();
            originals.Clear();
        }

        // Every mesh this mod made, and every mesh it replaced. A junction mesh
        // drawn for a closed junction's road that is none of these, and not the
        // mesh of the road's composition, is a copy another mod draws in its place.
        internal void CollectKnown(HashSet<Entity> known)
        {
            foreach (var original in originals.Values) known.Add(original.m_Mesh);
            foreach (var pair in stripSources)
            {
                known.Add(pair.Key);
                known.Add(pair.Value);
            }
            foreach (var pair in variants)
            {
                known.Add(pair.Key);
                if (pair.Value.Mesh != Entity.Null) known.Add(pair.Value.Mesh);
            }
            foreach (var pair in layouts)
            {
                known.Add(pair.Key);
                known.Add(pair.Value.Source);
            }
        }

        internal enum StripState { Unavailable, Pending, Ready }

        // One through-road lane of a strip. The center is measured across the
        // strip from its middle, positive away from the median. The drop is the
        // depth at which the piece lies below the strip's surface.
        internal struct StripPiece
        {
            internal Entity Piece;
            internal float3 Size;
            internal float Center;
            internal float Drop;
            internal NetPieceFlags Flags;
        }

        private sealed class StripVariant
        {
            internal Entity Composition;
            internal Entity Template;
            internal Entity Mesh;
            internal uint Signature;
            internal StripPiece[] Lanes;
            internal float Width;
            internal float HalfLength;
        }

        private sealed class Variant
        {
            internal Layout Layout;
            internal Entity Template;
            internal Entity Mesh;
        }

        internal struct Layout
        {
            internal readonly Entity Source;
            internal readonly Entity Divider;
            internal readonly int PieceIndex;
            internal readonly float Width;
            internal readonly float Offset;
            internal readonly float HalfLength;
            internal readonly Entity Composition;

            internal Layout(Entity source, Entity divider, int pieceIndex, float width, float offset,
                float halfLength, Entity composition)
            {
                Source = source;
                Divider = divider;
                PieceIndex = pieceIndex;
                Width = width;
                Offset = offset;
                HalfLength = halfLength;
                Composition = composition;
            }
        }
    }
}
