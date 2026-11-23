using System;
using System.Collections.Generic;
using Game.Prefabs;
using Unity.Entities;

namespace ConditionalMedians2
{
    internal sealed class NativeSections
    {
        // The game carries the node upgrade mask through native composition selection.
        // Bit 31 is unused by the supported game build.
        internal const CompositionFlags.General Closed = (CompositionFlags.General)0x80000000u;
        // The closed bit sits on the junction, so the game gives it to every road
        // end there. A divided side street at a closed T-junction keeps its own
        // median end: StemCompositionSystem gives its end a composition whose mask
        // also carries this marker, on both sides because the game swaps the sides
        // of a start-node composition. Side bit 31 is unused by the game, which
        // strips it from road flags, so only masks the mod builds carry it.
        internal const CompositionFlags.Side Stem = (CompositionFlags.Side)0x80000000u;
        private const CompositionFlags.General JunctionFlags =
            CompositionFlags.General.Node | CompositionFlags.General.Intersection |
            CompositionFlags.General.DeadEnd | CompositionFlags.General.Crosswalk |
            CompositionFlags.General.MedianBreak | CompositionFlags.General.LevelCrossing;
        private const NetSectionFlags PhysicalMedian = NetSectionFlags.Median |
            NetSectionFlags.Underground | NetSectionFlags.Overhead | NetSectionFlags.Hidden;
        private const NetPieceFlags Divider = NetPieceFlags.BlockTraffic | NetPieceFlags.Surface | NetPieceFlags.HasMesh;
        private const NetPieceFlags BlockerMask = Divider | NetPieceFlags.PreserveShape |
            NetPieceFlags.HasRoadLanes | NetPieceFlags.HasBicycleLanes;

        private readonly EntityManager manager;
        private readonly Dictionary<Entity, PreparedRoad> prepared = new Dictionary<Entity, PreparedRoad>();
        // Road types with no median section the mod can close, which Prepare
        // leaves as they are. Remembered so side streets cost one lookup a frame.
        private readonly HashSet<Entity> undivided = new HashSet<Entity>();

        internal NativeSections(EntityManager manager) { this.manager = manager; }

        internal static bool IsDivider(NetCompositionPiece piece)
        {
            return (piece.m_SectionFlags & PhysicalMedian) == NetSectionFlags.Median &&
                (piece.m_PieceFlags & BlockerMask) == Divider && piece.m_Size.x > 0.1f;
        }

        // A raised piece on the road that stops traffic, closable or not: the
        // median of a road the mod can close, but also each of two medians
        // side by side, or a platform in the middle.
        internal static bool IsRaisedBlocker(NetCompositionPiece piece)
        {
            return (piece.m_PieceFlags & NetPieceFlags.BlockTraffic) != 0 && piece.m_Size.x > 0.1f &&
                (piece.m_SectionFlags & (NetSectionFlags.Underground | NetSectionFlags.Overhead | NetSectionFlags.Hidden)) == 0;
        }

        internal bool CanClose(Entity prefab, Entity composition)
        {
            if (!manager.HasBuffer<NetGeometrySection>(prefab) ||
                !manager.HasBuffer<NetCompositionPiece>(composition)) return false;
            var road = manager.GetBuffer<NetGeometrySection>(prefab, true);
            foreach (var piece in manager.GetBuffer<NetCompositionPiece>(composition, true))
            {
                if (!IsDivider(piece)) continue;
                foreach (var section in road)
                {
                    if ((section.m_Flags & PhysicalMedian) != NetSectionFlags.Median ||
                        (section.m_CompositionAll.m_General & Closed) != 0 ||
                        !manager.HasBuffer<NetSectionPiece>(section.m_Section)) continue;
                    foreach (var candidate in manager.GetBuffer<NetSectionPiece>(section.m_Section, true))
                    {
                        if (candidate.m_Piece == piece.m_Piece && IsDividerRule(candidate)) return true;
                    }
                }
            }
            return false;
        }

        internal bool IsPrepared(Entity prefab) =>
            prepared.TryGetValue(prefab, out var known) && IsPrepared(prefab, known);

        // Whether a road type has a built divider the mod can close, read from
        // the prefab alone: on the frame a city loads, no road has selected its
        // compositions yet.
        internal bool HasDivider(Entity prefab)
        {
            if (prepared.ContainsKey(prefab)) return true;
            if (undivided.Contains(prefab) || !manager.HasBuffer<NetGeometrySection>(prefab)) return false;
            foreach (var section in manager.GetBuffer<NetGeometrySection>(prefab, true))
            {
                if ((section.m_Flags & PhysicalMedian) != NetSectionFlags.Median ||
                    !manager.HasBuffer<NetSectionPiece>(section.m_Section)) continue;
                foreach (var piece in manager.GetBuffer<NetSectionPiece>(section.m_Section, true))
                    if (IsDividerRule(piece)) return true;
            }
            return false;
        }

        internal void Prepare(Entity prefab)
        {
            if (undivided.Contains(prefab)) return;
            if (prepared.TryGetValue(prefab, out var known) && IsPrepared(prefab, known)) return;
            var original = manager.GetBuffer<NetGeometrySection>(prefab).ToNativeArray(Unity.Collections.Allocator.Temp);
            var added = new List<Entity>();
            var result = new List<NetGeometrySection>(original.Length + 4);
            try
            {
                for (int index = 0; index < original.Length; index++)
                {
                    var section = original[index];
                    if ((section.m_Flags & PhysicalMedian) != NetSectionFlags.Median ||
                        (section.m_CompositionAll.m_General & Closed) != 0 ||
                        !manager.HasBuffer<NetSectionPiece>(section.m_Section) ||
                        (manager.HasBuffer<NetSubSection>(section.m_Section) &&
                         manager.GetBuffer<NetSubSection>(section.m_Section, true).Length != 0))
                    {
                        result.Add(section);
                        continue;
                    }
                    var rules = new List<NetSectionPiece>();
                    bool hasDivider = false;
                    foreach (var source in manager.GetBuffer<NetSectionPiece>(section.m_Section, true))
                    {
                        var rule = source;
                        if (IsDividerRule(rule))
                        {
                            rule.m_CompositionNone.m_General &= ~JunctionFlags;
                            // The road's divider blocks crosswalks. Where the junction
                            // has crosswalks, the closed divider lets them through,
                            // one on each carriageway, and pedestrians cross over the
                            // closed median, as at the open junction. Where it has
                            // none (removed with the Crosswalk tool, or a plain road
                            // node) the divider still blocks them, as the road's
                            // divider does there, so no unmarked crossing appears.
                            var crossed = rule;
                            crossed.m_CompositionAll.m_General |= CompositionFlags.General.Crosswalk;
                            crossed.m_Flags &= ~NetPieceFlags.BlockCrosswalk;
                            rules.Add(crossed);
                            rule.m_CompositionNone.m_General |= CompositionFlags.General.Crosswalk;
                            hasDivider = true;
                        }
                        else if ((rule.m_Flags & NetPieceFlags.Surface) != 0)
                        {
                            continue;
                        }
                        rules.Add(rule);
                    }
                    if (!hasDivider)
                    {
                        result.Add(section);
                        continue;
                    }

                    Entity variant = manager.CreateEntity(typeof(NetSectionPiece), typeof(NetSubSection));
                    var nativeRules = manager.GetBuffer<NetSectionPiece>(variant);
                    foreach (var rule in rules) nativeRules.Add(rule);
                    added.Add(variant);

                    // Three copies take the original section's position across the
                    // road, and any mask selects exactly one of them: the road's own
                    // section at an open junction and along the road, the closed
                    // variant on a closed junction's two through approaches, and the
                    // road's own section again on a side street end marked as a stem.
                    var open = section;
                    open.m_CompositionNone.m_General |= Closed;
                    var closed = section;
                    closed.m_Section = variant;
                    closed.m_CompositionAll.m_General |= Closed;
                    closed.m_CompositionNone.m_Left |= Stem;
                    closed.m_CompositionNone.m_Right |= Stem;
                    var stem = section;
                    stem.m_CompositionAll.m_General |= Closed;
                    stem.m_CompositionAll.m_Left |= Stem;
                    stem.m_CompositionAll.m_Right |= Stem;
                    result.Add(open);
                    result.Add(closed);
                    result.Add(stem);
                }
                if (added.Count == 0)
                {
                    undivided.Add(prefab);
                    return;
                }
                // Creating the variants moved the prefab's buffers, so fetch it now.
                var buffer = manager.GetBuffer<NetGeometrySection>(prefab);
                buffer.Clear();
                foreach (var section in result) buffer.Add(section);
                // The game keeps every composition it builds, under its mask, and
                // builds a mask only once, from the sections as they were then. A
                // closed-junction mask built before this road type was prepared (a
                // side street on the frame a city loads, or a tool preview) would
                // keep a composition without the closed variant for the rest of the
                // session. Drop those, so the game builds them again from these
                // sections; a road that still uses one keeps it until it is
                // reselected.
                int dropped = 0;
                var built = manager.HasBuffer<NetGeometryComposition>(prefab)
                    ? manager.GetBuffer<NetGeometryComposition>(prefab) : default;
                for (int i = built.IsCreated ? built.Length - 1 : -1; i >= 0; i--)
                {
                    if ((built[i].m_Mask.m_General & Closed) == 0) continue;
                    built.RemoveAt(i);
                    dropped++;
                }
                if (dropped != 0)
                    Mod.Log.Info($"Road type {prefab.Index}: {dropped} closed-junction composition(s) built before the closed median was prepared will be built again.");
                prepared[prefab] = new PreparedRoad(added);
            }
            finally
            {
                original.Dispose();
            }
        }

        private bool IsPrepared(Entity prefab, PreparedRoad road)
        {
            if (road.Variants.Count == 0) return false;
            foreach (var section in manager.GetBuffer<NetGeometrySection>(prefab, true))
                if (section.m_Section == road.Variants[0]) return true;
            return false;
        }

        private bool IsDividerRule(NetSectionPiece piece)
        {
            if ((piece.m_Flags & BlockerMask) != Divider ||
                (piece.m_CompositionAll.m_General & JunctionFlags) != 0 ||
                (piece.m_CompositionAny.m_General & JunctionFlags) != 0 ||
                !manager.HasComponent<NetPieceData>(piece.m_Piece)) return false;
            return manager.GetComponentData<NetPieceData>(piece.m_Piece).m_Width > 0.1f;
        }

        private sealed class PreparedRoad
        {
            internal readonly List<Entity> Variants;
            internal PreparedRoad(List<Entity> variants) { Variants = variants; }
        }
    }
}
