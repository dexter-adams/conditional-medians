using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace ConditionalMedians2
{
    // A closed junction carries the closed bit on the node, and the game adds a
    // node's flags to the junction end of every road there. At a T-junction
    // whose side street is a divided road of a prepared road type, the side
    // street's end would therefore select the closed median variant and draw its
    // own divider into the junction. This system runs after the game has picked
    // the compositions and before anything reads them, and moves such an end to
    // a composition whose mask also carries the stem marker. The prepared road
    // type gives that mask the road's own median section, so the side street
    // ends as it does at an open junction. The mask keeps the closed bit, so the
    // composition is still used only at closed junctions, which the through-lane
    // strip relies on.
    public sealed partial class StemCompositionSystem : GameSystemBase
    {
        private EntityQuery closures;
        private MedianSystem medians;
        private readonly HashSet<(Entity Node, Entity Edge, Entity Composition)> reported =
            new HashSet<(Entity, Entity, Entity)>();

        protected override void OnCreate()
        {
            base.OnCreate();
            closures = GetEntityQuery(ComponentType.ReadOnly<ClosedMedian>(), ComponentType.ReadOnly<Upgraded>(),
                ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.Exclude<Deleted>());
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
        }

        protected override void OnUpdate()
        {
            if (closures.IsEmptyIgnoreFilter) return;
            using (var nodes = closures.ToEntityArray(Allocator.Temp))
            foreach (var node in nodes)
            {
                if ((EntityManager.GetComponentData<Upgraded>(node).m_Flags.m_General & NativeSections.Closed) == 0) continue;
                var placement = EntityManager.GetComponentData<ClosedMedian>(node);
                using (var edges = EntityManager.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Allocator.Temp))
                {
                    // While a through road is being replaced (a road built onto it
                    // splits it), its new segment is not yet the closure's: it is
                    // no side street, and is left alone until the closure has
                    // followed it.
                    bool first = false, second = false;
                    foreach (var item in edges)
                    {
                        first |= item.m_Edge == placement.FirstRoad;
                        second |= item.m_Edge == placement.SecondRoad;
                    }
                    if (!first || !second) continue;
                    foreach (var item in edges)
                    {
                        Entity edge = item.m_Edge;
                        if (edge == placement.FirstRoad || edge == placement.SecondRoad) continue;
                        MarkStem(node, edge);
                    }
                }
            }
        }

        private void MarkStem(Entity node, Entity edge)
        {
            if (!EntityManager.HasComponent<Edge>(edge) || !EntityManager.HasComponent<Composition>(edge) ||
                !EntityManager.HasComponent<PrefabRef>(edge) || EntityManager.HasComponent<Deleted>(edge) ||
                EntityManager.HasComponent<Temp>(edge)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            if (!medians.IsPreparedRoad(prefab) || !EntityManager.HasBuffer<NetGeometryComposition>(prefab) ||
                !EntityManager.HasComponent<NetGeometryData>(prefab)) return;
            var ends = EntityManager.GetComponentData<Edge>(edge);
            bool starts = ends.m_Start == node;
            if (!starts && ends.m_End != node) return;
            var composition = EntityManager.GetComponentData<Composition>(edge);
            Entity current = starts ? composition.m_StartNode : composition.m_EndNode;
            if (!TryMask(prefab, current, out var mask) ||
                (mask.m_General & NativeSections.Closed) == 0 ||
                (mask.m_General & CompositionFlags.General.Node) == 0 ||
                (mask.m_Left & NativeSections.Stem) != 0) return;

            var wanted = mask;
            wanted.m_Left |= NativeSections.Stem;
            wanted.m_Right |= NativeSections.Stem;
            Entity replacement = Find(prefab, wanted);
            bool created = replacement == Entity.Null;
            if (created) replacement = Create(prefab, wanted);
            if (starts) composition.m_StartNode = replacement;
            else composition.m_EndNode = replacement;
            EntityManager.SetComponentData(edge, composition);
            // The game's geometry and lane systems rebuild only updated roads.
            // This normally runs in the frame the game reselected the road's
            // compositions, which already marked it.
            if (!EntityManager.HasComponent<Updated>(edge)) EntityManager.AddComponent<Updated>(edge);
            if (reported.Add((node, edge, replacement)))
                Mod.Log.Info($"Junction {node.Index}: the divided side street {edge.Index} keeps its own median end " +
                    $"(composition {current.Index} to {replacement.Index}{(created ? ", new" : "")}).");
        }

        // The mask the game selected a composition by, from the road type's list.
        private bool TryMask(Entity prefab, Entity composition, out CompositionFlags mask)
        {
            mask = default;
            if (composition == Entity.Null) return false;
            foreach (var entry in EntityManager.GetBuffer<NetGeometryComposition>(prefab, true))
            {
                if (entry.m_Composition != composition) continue;
                mask = entry.m_Mask;
                return true;
            }
            return false;
        }

        private Entity Find(Entity prefab, CompositionFlags mask)
        {
            foreach (var entry in EntityManager.GetBuffer<NetGeometryComposition>(prefab, true))
                if (entry.m_Mask == mask && EntityManager.Exists(entry.m_Composition)) return entry.m_Composition;
            return Entity.Null;
        }

        // Builds a node composition the way the game's CompositionSelectSystem
        // does: an entity of the road type's node composition archetype, which
        // carries Created so the game's composition and mesh systems set it up
        // this frame, with the pieces the road type's sections give for the mask,
        // registered in the road type's list under that mask.
        private Entity Create(Entity prefab, CompositionFlags mask)
        {
            var geometry = EntityManager.GetComponentData<NetGeometryData>(prefab);
            Entity entity = EntityManager.CreateEntity(geometry.m_NodeCompositionArchetype);
            EntityManager.GetBuffer<NetGeometryComposition>(prefab).Add(new NetGeometryComposition
            {
                m_Composition = entity,
                m_Mask = mask
            });
            EntityManager.SetComponentData(entity, new PrefabRef(prefab));
            EntityManager.SetComponentData(entity, new NetCompositionData { m_Flags = mask });
            var pieces = new NativeList<NetCompositionPiece>(32, Allocator.Temp);
            try
            {
                using (var sections = EntityManager.GetBuffer<NetGeometrySection>(prefab, true).ToNativeArray(Allocator.Temp))
                    NetCompositionHelpers.GetCompositionPieces(pieces, sections, mask,
                        GetBufferLookup<NetSubSection>(true), GetBufferLookup<NetSectionPiece>(true));
                EntityManager.GetBuffer<NetCompositionPiece>(entity).CopyFrom(pieces.AsArray());
                for (int i = 0; i < pieces.Length; i++)
                {
                    if (!EntityManager.HasComponent<NetTerrainData>(pieces[i].m_Piece)) continue;
                    EntityManager.AddComponent<TerrainComposition>(entity);
                    break;
                }
            }
            finally
            {
                pieces.Dispose();
            }
            return entity;
        }
    }
}
