using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Entities;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using ConnectedEdge = Game.Net.ConnectedEdge;
using Curve = Game.Net.Curve;
using Lane = Game.Net.Lane;
using PathNode = Game.Pathfind.PathNode;
using SubLane = Game.Net.SubLane;

namespace ConditionalMedians2
{
    // The median edge lines of a closed junction's two approaches, and the
    // curves that join them across the junction. A road's lines are marking
    // lanes owned by its segments that end at the junction mouth: the game's
    // own (SecondaryLaneSystem) or, with Curbside, Curbside's, which hides the
    // game's line it replaces. Curbside's lines name the road as their owner
    // but are not listed in its sub-lanes, so lines are found by owner. The
    // continuation copies whichever line the approach shows, so it has its
    // prefab, colour and offset.
    internal static class MedianLines
    {
        // Drawn lanes that carry no traffic: road markings, hidden ones
        // included (a tool preview hides a road's stop bar for a moment, and
        // the line must stay cut at it, as Curbside's does).
        internal static EntityQueryDesc MarkingQuery => new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Game.Net.LaneGeometry>(),
                ComponentType.ReadOnly<CullingInfo>(), ComponentType.ReadOnly<MeshBatch>(),
                ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Owner>() },
            None = new[] { ComponentType.ReadOnly<CarLane>(), ComponentType.ReadOnly<Game.Net.PedestrianLane>(),
                ComponentType.ReadOnly<Game.Net.TrackLane>(), ComponentType.ReadOnly<Game.Net.ParkingLane>(),
                ComponentType.ReadOnly<Game.Net.ConnectionLane>(), ComponentType.ReadOnly<Game.Net.UtilityLane>(),
                ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(),
                ComponentType.ReadOnly<MedianLineMarking>() }
        };

        // How far from the median's center line an edge line may lie, beyond
        // the divider's half width, and how far before the junction it may end.
        private const float MaximumOffset = 2.0f;
        private const float MaximumGap = 4.0f;
        // The length of line, back from its end, that must run along the median.
        private const float EndSample = 3f;

        internal struct Source
        {
            internal Entity Lane;
            internal Entity Prefab;
            // The end at the junction and the direction there, pointing into the junction.
            internal float3 End;
            internal float3 Inward;
            // Station of the end: its distance from the median's end, measured
            // outward along the road, negative inside the junction.
            internal float Reach;
            // The road at the median's end: where it meets the junction, its
            // direction away from the junction, and the line's distance across it.
            internal float2 Mouth;
            internal float2 Outward;
            internal float2 Across;
            internal float Lateral;
            // The stop bar the line meets in front of its end, if any: its
            // station where it crosses the line, and its painted width.
            internal Entity StopBar;
            internal float StopStation;
            internal float StopWidth;
            // Where Curbside cuts its line: the farthest stop bar crossing.
            internal bool Cut;
            internal float CutStation;
        }

        // A stop bar may lie this far past the line's end and still count, for
        // a Curbside setback of zero.
        private const float EndTolerance = 0.05f;

        // A stop bar is a marking across the road (within 30 degrees of square
        // to it, Curbside's limit) on the line's side of the median, crossing
        // the line between the mouth and the line's end; its span comes within
        // this distance of the line. A marking that spans both sides of the
        // median (such as Curbside's crosswalk lines) is not a stop bar.
        private const float StopSquareness = 0.5f;
        private const float StopReach = 0.5f;
        private const float StopAxisTolerance = 0.3f;
        // Curbside ends its median line its setback plus half the bar's width
        // past the point where the bar crosses it (0.55 m by default); a line
        // that ends nearer than the bare setback takes that gap from the bar.
        private const float DefaultSetback = 0.05f;

        // A road's direction at the junction mouth, pointing away from the
        // junction, from the road's drawn edges there. On a curved road this
        // differs from its direction at the junction's center.
        internal static bool Outward(EntityManager entities, Entity edge, Entity node, out float2 outward)
        {
            outward = default;
            if (!entities.TryGetComponent<Game.Net.Edge>(edge, out var ends) ||
                !entities.TryGetComponent<Game.Net.EdgeGeometry>(edge, out var shape)) return false;
            float3 tangent = ends.m_Start == node
                ? math.normalizesafe(MathUtils.StartTangent(shape.m_Start.m_Left)) + math.normalizesafe(MathUtils.StartTangent(shape.m_Start.m_Right))
                : -math.normalizesafe(MathUtils.EndTangent(shape.m_End.m_Left)) - math.normalizesafe(MathUtils.EndTangent(shape.m_End.m_Right));
            if (math.lengthsq(tangent.xz) < 0.0001f) return false;
            outward = math.normalize(tangent.xz);
            return true;
        }

        // A drawn lane that carries no traffic and is not hidden: a road
        // marking someone can see, the game's or another mod's.
        internal static bool IsVisibleMarking(EntityManager entities, Entity lane) =>
            IsMarking(entities, lane) && !entities.HasComponent<Game.Tools.Hidden>(lane);

        // A drawn lane that carries no traffic, hidden or not.
        internal static bool IsMarking(EntityManager entities, Entity lane)
        {
            return entities.HasComponent<Curve>(lane) && entities.HasComponent<Game.Net.LaneGeometry>(lane) &&
                entities.HasComponent<CullingInfo>(lane) && entities.HasComponent<MeshBatch>(lane) &&
                entities.HasComponent<PrefabRef>(lane) &&
                !entities.HasComponent<CarLane>(lane) && !entities.HasComponent<Game.Net.PedestrianLane>(lane) &&
                !entities.HasComponent<Game.Net.TrackLane>(lane) && !entities.HasComponent<Game.Net.ParkingLane>(lane) &&
                !entities.HasComponent<Game.Net.ConnectionLane>(lane) && !entities.HasComponent<Game.Net.UtilityLane>(lane) &&
                !entities.HasComponent<Deleted>(lane) && !entities.HasComponent<Game.Tools.Temp>(lane) &&
                !entities.HasComponent<MedianLineMarking>(lane);
        }

        // For each side of the median (0: left of its axis, 1: right), the
        // edge line of one approach that ends closest to the junction. The
        // sign is +1 for the approach on the positive end of the median's
        // axis and -1 for the other; outward is the road's direction at the
        // junction, pointing away from it. Each line is measured against its
        // own road, which may meet the median's axis at an angle.
        // A painted median's own lines lie on or beside its axis: a center line
        // (single, or double drawn as one marking) on it, or a pair of lines a
        // little either side, within the paint's half width plus this. Curbside
        // draws the edge lines of a painted strip inside the lanes, 0.35 m past
        // the strip's edge by default; the next lines out, between lanes of one
        // direction, lie a whole lane width further.
        private const float CenterReach = 0.75f;
        private const float CenterTolerance = 0.08f;

        internal static void FindSources(EntityManager entities, List<Entity> lanes, float sign, float2 outward,
            MedianGeometry median, Source[] found, bool[] has, System.Text.StringBuilder why = null)
        {
            bool center = median.Painted;
            has[0] = has[1] = false;
            if (lanes == null)
            {
                why?.Append(" no markings listed;");
                return;
            }
            float2 mouth = median.Center.xz + median.Axis * (median.HalfLength * sign);
            // Across the road, signed like the median's own lateral axis.
            float2 axis = outward * sign;
            float2 across = new float2(-axis.y, axis.x);
            why?.Append($" {lanes.Count} markings, outward ({outward.x:F2},{outward.y:F2}):");
            foreach (var lane in lanes)
            {
                if (!IsVisibleMarking(entities, lane)) continue;
                Entity prefab = entities.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (!entities.HasComponent<NetLaneArchetypeData>(prefab)) continue;
                var curve = entities.GetComponentData<Curve>(lane);
                if (curve.m_Length < 1f) continue;
                var bezier = curve.m_Bezier;
                bool endAtD = math.distancesq(bezier.d.xz, mouth) < math.distancesq(bezier.a.xz, mouth);
                float share = math.min(0.5f, EndSample / curve.m_Length);
                float3 end = endAtD ? bezier.d : bezier.a;
                float2 e = end.xz - mouth;
                float2 b = MathUtils.Position(bezier, endAtD ? 1f - share : share).xz - mouth;
                float ez = math.dot(e, across);
                float bz = math.dot(b, across);
                float reach = math.dot(e, outward);
                string rejected =
                    // A painted median's line on or beside its axis; a divider's
                    // edge line beside it on one side. Either running along the
                    // road near its end.
                    center ? (math.abs(ez) > median.HalfWidth + CenterReach || math.abs(bz) > median.HalfWidth + CenterReach ? "offset" : null) :
                    ez * bz <= 0f ? "crosses the axis" :
                    math.abs(ez) < median.HalfWidth * 0.5f || math.abs(ez) > median.HalfWidth + MaximumOffset ? "offset" : null;
                rejected = rejected ??
                    (math.abs(ez - bz) > 0.3f ? "not parallel" :
                    // Ending at or before the junction mouth, coming from this approach.
                    reach < -1f || reach > MaximumGap ? "ends elsewhere" :
                    math.dot(b, outward) - reach < 1f ? "points away" : null);
                if (math.abs(reach) < 8f)
                    why?.Append($" {lane.Index} end ({reach:F2},{ez:F2}) back {bz:F2} {rejected ?? "kept"};");
                if (rejected != null) continue;
                // A center line on the axis counts as the left one, so both
                // approaches agree on it whatever side of zero it reads.
                int side = center && math.abs(ez) <= CenterTolerance ? 0 : ez > 0f ? 0 : 1;
                if (has[side] && found[side].Reach <= reach) continue;
                found[side] = new Source
                {
                    Lane = lane,
                    Prefab = prefab,
                    End = end,
                    Inward = endAtD ? MathUtils.EndTangent(bezier) : -MathUtils.StartTangent(bezier),
                    Reach = reach,
                    Mouth = mouth,
                    Outward = outward,
                    Across = across,
                    // A center line on the axis belongs to both sides, so the
                    // stop bar of either approach's incoming side can cut it.
                    Lateral = center && math.abs(ez) <= CenterTolerance ? 0f : ez
                };
                has[side] = true;
            }
        }

        // Finds the stop bar the line meets in front of its end among the
        // given markings (the approach's or the junction's), keeping the one
        // nearest the line's end. Curbside stops its median line a little way
        // behind the stop bar; the continuation leaves the same gap on the
        // bar's other side, so the bar stands clear of the line as Curbside
        // draws it.
        internal static void FindStop(EntityManager entities, List<Entity> lanes, Func<Entity, bool> isStopLine,
            ref Source source, System.Text.StringBuilder why = null)
        {
            if (lanes == null) return;
            foreach (var lane in lanes)
            {
                // Hidden bars count: only a tool preview hides one, and Curbside's
                // line stays cut at it meanwhile.
                if (lane == source.Lane || !IsMarking(entities, lane)) continue;
                // Stop lines only, as Curbside reads them: not crosswalk lines, yield
                // lines or other markings across the road.
                if (!isStopLine(entities.GetComponentData<PrefabRef>(lane).m_Prefab)) continue;
                var curve = entities.GetComponentData<Curve>(lane);
                if (curve.m_Length < 1f) continue;
                float2 a = curve.m_Bezier.a.xz - source.Mouth;
                float2 d = curve.m_Bezier.d.xz - source.Mouth;
                float2 chord = d - a;
                float length = math.length(chord);
                if (length < 1f || math.abs(math.dot(chord / length, source.Outward)) > StopSquareness) continue;
                float za = math.dot(a, source.Across);
                float zd = math.dot(d, source.Across);
                // Where the bar's line crosses the line, as Curbside measures it, so
                // a skewed bar is placed right.
                float u = math.abs(zd - za) > 0.01f ? (source.Lateral - za) / (zd - za) : 0.5f;
                float station = math.dot(math.lerp(a, d, u), source.Outward);
                // In front of the line's end (within Curbside's zero setback), toward
                // the junction, and not inside it.
                if (station < -1f || station > source.Reach + EndTolerance) continue;
                // Curbside cuts its line at the farthest such bar, wherever it lies across
                // the road; its gap is measured from there.
                if (!source.Cut || station > source.CutStation)
                {
                    source.Cut = true;
                    source.CutStation = station;
                }
                // The bar that meets the line: on its side of the median, reaching it.
                if (source.Lateral < math.min(za, zd) - StopReach || source.Lateral > math.max(za, zd) + StopReach) continue;
                float side = math.sign(source.Lateral);
                if (math.min(za * side, zd * side) < -StopAxisTolerance) continue;
                if (source.StopBar != Entity.Null && station <= source.StopStation) continue;
                Entity prefab = entities.GetComponentData<PrefabRef>(lane).m_Prefab;
                source.StopBar = lane;
                source.StopStation = station;
                source.StopWidth = entities.TryGetComponent<NetLaneGeometryData>(prefab, out var size) ? size.m_Size.x : 0f;
                why?.Append($" stop bar {lane.Index} at {station:F2} span {math.min(za, zd):F2}..{math.max(za, zd):F2};");
            }
        }

        // The gap the continuation leaves on the junction side of its stop bar:
        // as far as Curbside's line ends past its cut, or, for a line that ends
        // at the cut, Curbside's default setback and half the bar's width.
        internal static float StopGap(Source source)
        {
            float setback = source.Reach - (source.Cut ? source.CutStation : source.StopStation);
            return setback >= DefaultSetback ? setback : DefaultSetback + source.StopWidth * 0.5f;
        }

        // The curve parameter where the continuation starts (at its start) or
        // ends (at its end) for a line that meets a stop bar: the station on
        // the junction's side of the bar, the gap away from its center line.
        // Without a stop bar the continuation keeps the line's end.
        internal static float Cut(Bezier4x3 curve, Source source, bool atStart)
        {
            if (source.StopBar == Entity.Null) return atStart ? 0f : 1f;
            float target = source.StopStation - StopGap(source);
            // The station falls from the line's end toward the junction's center.
            float near = atStart ? 0f : 1f;
            float far = 0.5f;
            if (Station(curve, far, source) > target) return far;
            for (int i = 0; i < 32; i++)
            {
                float middle = (near + far) * 0.5f;
                if (Station(curve, middle, source) > target) near = middle;
                else far = middle;
            }
            return (near + far) * 0.5f;
        }

        private static float Station(Bezier4x3 curve, float t, Source source) =>
            math.dot(MathUtils.Position(curve, t).xz - source.Mouth, source.Outward);

        // A marked crosswalk as drawn: the straight line it is painted along,
        // the stretch of that line that is painted, and the paint's half width
        // to each side of it.
        internal struct Crosswalk
        {
            internal Entity Lane;
            internal float2 Start;
            internal float2 Along;
            internal float2 Normal;
            internal float Length;
            internal float From;
            internal float To;
            internal float HalfWidth;
            internal bool Lined;
        }

        // A stretch of the joined curve, as curve parameters, that is not
        // drawn: in front of a stop bar or across a crosswalk.
        internal struct Gap
        {
            internal float From;
            internal float To;
            internal bool Stop;
            // The junction mouth it lies at: 0 for the first approach's, 1 for the second's.
            internal int Mouth;
        }

        // The mouth a stretch of the joined curve lies nearer to, measured from
        // each approach's median end along its own road.
        internal static int NearerMouth(Bezier4x3 curve, float from, float to, Source first, Source second)
        {
            float2 point = MathUtils.Position(curve, (from + to) * 0.5f).xz;
            float a = math.abs(math.dot(point - first.Mouth, first.Outward));
            float b = math.abs(math.dot(point - second.Mouth, second.Outward));
            return a <= b ? 0 : 1;
        }

        // The line keeps this much clear of a crosswalk's paint, as Curbside
        // keeps its line clear of a stop bar.
        private const float CrosswalkGap = 0.05f;
        // Curbside's lined crosswalk style: two lines along the crossing, within
        // this distance of it on either side.
        private const float LinedReach = 3.1f;

        // The marked crosswalks of a junction, hidden ones included (a tool
        // preview hides them for a moment, and the line stays cut at them).
        // The game makes every crosswalk a lane of its junction; it paints the
        // lane's own mesh, trimmed at a skewed mouth, unless the crossing is
        // unmarked. Curbside's lined style hides that paint and draws two lines
        // along the crossing instead, owned by the junction; its invisible
        // style draws nothing.
        internal static void FindCrosswalks(EntityManager entities, Entity node, List<Entity> markings,
            List<Crosswalk> found, System.Text.StringBuilder why = null)
        {
            found.Clear();
            if (!entities.HasBuffer<SubLane>(node)) return;
            foreach (var item in entities.GetBuffer<SubLane>(node, true))
            {
                Entity lane = item.m_SubLane;
                if (!entities.TryGetComponent<Game.Net.PedestrianLane>(lane, out var walk) ||
                    (walk.m_Flags & Game.Net.PedestrianLaneFlags.Crosswalk) == 0 ||
                    (walk.m_Flags & Game.Net.PedestrianLaneFlags.Unsafe) != 0) continue;
                if (entities.HasComponent<Deleted>(lane) || entities.HasComponent<Game.Tools.Temp>(lane) ||
                    entities.HasComponent<Overridden>(lane)) continue;
                if (!entities.TryGetComponent<Curve>(lane, out var curve) || curve.m_Length < 0.1f ||
                    !entities.TryGetComponent<PrefabRef>(lane, out var prefab) ||
                    !entities.TryGetComponent<NetLaneGeometryData>(prefab.m_Prefab, out var geometry)) continue;
                float2 start = curve.m_Bezier.a.xz;
                float2 chord = curve.m_Bezier.d.xz - start;
                float length = math.length(chord);
                if (length < 0.5f) continue;
                var crossing = new Crosswalk { Lane = lane, Start = start, Along = chord / length, Length = length, To = length };
                crossing.Normal = new float2(-crossing.Along.y, crossing.Along.x);
                if (geometry.m_GameLayers != default(Game.Prefabs.MeshLayer))
                {
                    // The game leaves the ends of a crosswalk met at an angle unpainted.
                    if (entities.HasBuffer<Game.Net.CutRange>(lane))
                        foreach (var range in entities.GetBuffer<Game.Net.CutRange>(lane, true))
                        {
                            if (range.m_CurveDelta.min <= 0.001f) crossing.From = math.max(crossing.From, length * range.m_CurveDelta.max);
                            if (range.m_CurveDelta.max >= 0.999f) crossing.To = math.min(crossing.To, length * range.m_CurveDelta.min);
                        }
                    crossing.HalfWidth = CrosswalkWidth(entities, lane, prefab.m_Prefab, geometry) * 0.5f;
                    if (crossing.HalfWidth < 0.05f) continue;
                }
                else if (!PaintedLines(entities, markings, ref crossing)) continue;
                if (crossing.To - crossing.From < 0.1f) continue;
                found.Add(crossing);
                why?.Append($" crosswalk {lane.Index}{(crossing.Lined ? " lined" : "")} {crossing.From:F2}..{crossing.To:F2} of {length:F2} m, half width {crossing.HalfWidth:F2};");
            }
        }

        // The width the game paints a crosswalk along the road: its mesh's
        // width across the lane, widened at a junction by the lane's width
        // offset over its own width at each end; the wider end counts.
        private static float CrosswalkWidth(EntityManager entities, Entity lane, Entity prefab, NetLaneGeometryData geometry)
        {
            float scale = 1f;
            if (entities.TryGetComponent<NetLaneData>(prefab, out var data) && data.m_Width > 0f &&
                entities.TryGetComponent<Game.Net.NodeLane>(lane, out var node))
                scale = 1f + math.cmax(node.m_WidthOffset) / data.m_Width;
            return geometry.m_Size.x * math.max(scale, 0f);
        }

        // Curbside's lined crosswalk: the junction's markings that run along
        // the crossing, beside it and overlapping it. The paint spans out to
        // the farther line's outer edge on both sides (Curbside centres the two
        // lines on the crossing) and along the lines' own extent.
        private static bool PaintedLines(EntityManager entities, List<Entity> markings, ref Crosswalk crossing)
        {
            if (markings == null) return false;
            bool any = false;
            float half = 0f, from = float.MaxValue, to = float.MinValue;
            foreach (var lane in markings)
            {
                if (!IsMarking(entities, lane)) continue;
                var bezier = entities.GetComponentData<Curve>(lane).m_Bezier;
                float2 a = bezier.a.xz - crossing.Start;
                float2 d = bezier.d.xz - crossing.Start;
                float2 chord = d - a;
                float length = math.length(chord);
                if (length < 0.5f || math.abs(math.dot(chord / length, crossing.Along)) < 0.99f) continue;
                float offset = math.abs(math.dot((a + d) * 0.5f, crossing.Normal));
                if (offset < 0.1f || offset > LinedReach) continue;
                float ua = math.dot(a, crossing.Along);
                float ud = math.dot(d, crossing.Along);
                float low = math.min(ua, ud), high = math.max(ua, ud);
                if (math.min(high, crossing.Length) - math.max(low, 0f) < 0.5f) continue;
                Entity prefab = entities.GetComponentData<PrefabRef>(lane).m_Prefab;
                float width = entities.TryGetComponent<NetLaneGeometryData>(prefab, out var size) ? size.m_Size.x : 0f;
                half = math.max(half, offset + width * 0.5f);
                from = math.min(from, low);
                to = math.max(to, high);
                any = true;
            }
            if (!any) return false;
            crossing.HalfWidth = half;
            crossing.From = from;
            crossing.To = to;
            crossing.Lined = true;
            return true;
        }

        // Adds the stretches of the curve that run over a crosswalk's paint or
        // the gap beside it, with the line's own half width counted, as gaps.
        // Returns how many crosswalks cut the curve.
        internal static int CutAtCrosswalks(Bezier4x3 curve, List<Crosswalk> crossings, float lineHalf,
            List<Gap> gaps, System.Text.StringBuilder why = null)
        {
            if (crossings.Count == 0) return 0;
            int count = 0;
            // About every quarter metre, so no crosswalk is stepped over.
            int samples = math.max(64, (int)math.ceil(MathUtils.Length(curve) / 0.25f));
            foreach (var crossing in crossings)
            {
                bool cut = false;
                bool inside = Inside(curve, 0f, crossing, lineHalf);
                float entered = 0f;
                for (int i = 1; i <= samples; i++)
                {
                    float t = (float)i / samples;
                    bool next = Inside(curve, t, crossing, lineHalf);
                    if (next == inside) continue;
                    float edge = Boundary(curve, (float)(i - 1) / samples, t, inside, crossing, lineHalf);
                    if (next) entered = edge;
                    else
                    {
                        gaps.Add(new Gap { From = entered, To = edge });
                        why?.Append($" crosswalk {crossing.Lane.Index} cuts {entered:F3}..{edge:F3};");
                        cut = true;
                    }
                    inside = next;
                }
                if (inside)
                {
                    gaps.Add(new Gap { From = entered, To = 1f });
                    why?.Append($" crosswalk {crossing.Lane.Index} cuts {entered:F3}..1;");
                    cut = true;
                }
                if (cut) count++;
            }
            return count;
        }

        // Whether the line, at a point of the curve, touches a crosswalk's paint
        // or comes within the gap of it: across the crossing, the paint's half
        // width, the gap, and the line's own half width as far as it reaches
        // across (all of it where the line runs along the crossing); along the
        // crossing, the painted stretch, the gap and the line's half width.
        private static bool Inside(Bezier4x3 curve, float t, Crosswalk crossing, float lineHalf)
        {
            float2 point = MathUtils.Position(curve, t).xz - crossing.Start;
            float2 tangent = math.normalizesafe(MathUtils.Tangent(curve, t).xz);
            float u = math.dot(point, crossing.Along);
            float v = math.dot(point, crossing.Normal);
            float reach = CrosswalkGap + lineHalf;
            return math.abs(v) <= crossing.HalfWidth + CrosswalkGap + lineHalf * math.abs(math.dot(tangent, crossing.Along)) &&
                u >= crossing.From - reach && u <= crossing.To + reach;
        }

        // The curve parameter between two samples where the line enters or
        // leaves a crosswalk's strip.
        private static float Boundary(Bezier4x3 curve, float low, float high, bool lowInside, Crosswalk crossing, float lineHalf)
        {
            for (int i = 0; i < 24; i++)
            {
                float middle = (low + high) * 0.5f;
                if (Inside(curve, middle, crossing, lineHalf) == lowInside) low = middle;
                else high = middle;
            }
            return (low + high) * 0.5f;
        }

        // The curve from one approach's line end to the other's, leaving and
        // arriving along each line, fitted the way the game joins lanes
        // across a junction.
        internal static Bezier4x3 Join(Source from, Source to)
        {
            return Game.Net.NetUtils.FitCurve(from.End, Flat(from.Inward), Flat(-to.Inward), to.End);
        }

        private static float3 Flat(float3 tangent) => tangent / math.max(0.0001f, math.length(tangent.xz));

        // Whether a visible marking of the junction itself already runs along
        // the curve, such as a line another mod draws through the junction.
        internal static bool Overlaps(EntityManager entities, List<Entity> lanes, Bezier4x3 curve)
        {
            if (lanes == null) return false;
            foreach (var lane in lanes)
            {
                if (!IsVisibleMarking(entities, lane)) continue;
                var other = entities.GetComponentData<Curve>(lane);
                if (other.m_Length < 1f) continue;
                float3 middle = MathUtils.Position(other.m_Bezier, 0.5f);
                if (MathUtils.Distance(curve.xz, middle.xz, out float t) > 0.5f) continue;
                float2 along = math.normalizesafe(MathUtils.Tangent(other.m_Bezier, 0.5f).xz);
                float2 ours = math.normalizesafe(MathUtils.Tangent(curve, t).xz);
                if (math.abs(math.dot(along, ours)) > 0.9f) return true;
            }
            return false;
        }

        // The stretches of the curve that a visible marking of the junction
        // itself already paints (the game draws a piece of center line through
        // a T-junction), added as cuts so a painted median's continuation only
        // fills what that line leaves. Mouth -1: such a cut pairs with no stop bar.
        internal static int CutAtLines(EntityManager entities, List<Entity> lanes, Bezier4x3 curve, List<Gap> cuts)
        {
            if (lanes == null) return 0;
            int count = 0;
            foreach (var lane in lanes)
            {
                if (!IsVisibleMarking(entities, lane)) continue;
                var other = entities.GetComponentData<Curve>(lane);
                if (other.m_Length < 1f) continue;
                float3 middle = MathUtils.Position(other.m_Bezier, 0.5f);
                if (MathUtils.Distance(curve.xz, middle.xz, out float t) > 0.5f) continue;
                float2 along = math.normalizesafe(MathUtils.Tangent(other.m_Bezier, 0.5f).xz);
                float2 ours = math.normalizesafe(MathUtils.Tangent(curve, t).xz);
                if (math.abs(math.dot(along, ours)) <= 0.9f) continue;
                MathUtils.Distance(curve.xz, other.m_Bezier.a.xz, out float ta);
                MathUtils.Distance(curve.xz, other.m_Bezier.d.xz, out float td);
                cuts.Add(new Gap { From = math.min(ta, td), To = math.max(ta, td), Stop = false, Mouth = -1 });
                count++;
            }
            return count;
        }

        internal static bool Close(Bezier4x3 a, Bezier4x3 b)
        {
            const float tolerance = 0.005f * 0.005f;
            return math.distancesq(a.a, b.a) < tolerance && math.distancesq(a.b, b.b) < tolerance &&
                math.distancesq(a.c, b.c) < tolerance && math.distancesq(a.d, b.d) < tolerance;
        }

        // True when the game's save leaves out entities carrying Unity's
        // Disabled tag, which is how these lines are kept out of saves.
        private static readonly FieldInfo SerializerQuery =
            typeof(Game.Serialization.SerializerSystem).GetField("m_Query", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool SaveSkipsDisabled(World world)
        {
            try
            {
                var serializer = world?.GetExistingSystemManaged<Game.Serialization.SerializerSystem>();
                if (serializer == null || SerializerQuery == null || SerializerQuery.FieldType != typeof(EntityQuery)) return false;
                var query = (EntityQuery)SerializerQuery.GetValue(serializer);
                var descriptions = query.GetEntityQueryDescs();
                if (descriptions == null || descriptions.Length == 0) return false;
                foreach (var description in descriptions)
                {
                    if (description == null || (description.Options & EntityQueryOptions.IncludeDisabledEntities) != 0) return false;
                    if (Names<Disabled>(description.All) || Names<Disabled>(description.Any) ||
                        Names<Disabled>(description.Present) || Names<Disabled>(description.Disabled)) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Names<T>(ComponentType[] types)
        {
            if (types == null) return false;
            var index = ComponentType.ReadOnly<T>().TypeIndex;
            foreach (var type in types)
                if (type.TypeIndex == index) return true;
            return false;
        }
    }

    // Marks the marking lanes this mod draws across closed junctions. They
    // have no owner, so neither the game's lane updates nor other mods'
    // marking tools treat them as a road's lanes, and they are never saved.
    public struct MedianLineMarking : IComponentData, IEmptySerializable
    {
    }

    // Continues the approaches' median edge lines through each junction whose
    // closed median is drawn. Runs in Modification5, after the game has laid
    // the frame's own lines and stop bars through ModificationBarrier4B and
    // before the lane search tree indexes new lanes, so a line created here is
    // drawn in the same frame. Curbside creates its lines later in
    // Modification5, so a respawned Curbside line is seen one frame late; the
    // grace below covers that.
    public sealed partial class MedianLineSystem : GameSystemBase
    {
        private const uint CheckInterval = 8;
        // Approach lines are deleted and drawn again when a road changes, and
        // Curbside withholds its lines from the start to the end of each save
        // (about 100 frames); the continuation waits this long for them before
        // it goes as well.
        private const uint GraceFrames = 300;
        private MedianSystem medians;
        private PrefabSystem prefabs;
        private EntityQuery tagged;
        private EntityQuery disabled;
        private EntityQuery markings;
        private EntityQuery roadPrefabs;
        // The stop line prefabs, as Curbside reads them: any lane a road prefab
        // places as a crossing that requires a stop, and the game's stop lines by
        // name. Gathered once per city.
        private readonly HashSet<Entity> stopLines = new HashSet<Entity>();
        private readonly Dictionary<Entity, bool> stopLineNames = new Dictionary<Entity, bool>();
        private bool stopLinesRead;
        private Func<Entity, bool> isStopLine;
        // The marked crosswalks of the junction being rebuilt.
        private readonly List<MedianLines.Crosswalk> crosswalks = new List<MedianLines.Crosswalk>();
        // Ranges of the joined curve, as curve parameters, that are not drawn,
        // and the same merged in order.
        private readonly List<MedianLines.Gap> cuts = new List<MedianLines.Gap>();
        private readonly List<Merged> merged = new List<Merged>();
        private readonly Dictionary<Entity, Junction> lines = new Dictionary<Entity, Junction>();
        private readonly HashSet<Entity> tracked = new HashSet<Entity>();
        private readonly List<Entity> scratch = new List<Entity>();
        private readonly List<Entity> pending = new List<Entity>();
        // The visible markings of the junctions and roads being rebuilt, by owner.
        private readonly Dictionary<Entity, List<Entity>> owned = new Dictionary<Entity, List<Entity>>();
        private readonly Stack<List<Entity>> spareLists = new Stack<List<Entity>>();
        private readonly MedianLines.Source[] first = new MedianLines.Source[2];
        private readonly MedianLines.Source[] second = new MedianLines.Source[2];
        private readonly bool[] hasFirst = new bool[2];
        private readonly bool[] hasSecond = new bool[2];
        private readonly List<Piece> desired = new List<Piece>();
        private bool sweep = true;
        private bool saveSafe;

        // Whether lines can be drawn: only when the game's save leaves out what
        // the mod hides while it writes. A painted median has nothing else to
        // draw, so the tool offers one only then.
        internal bool CanDraw => saveSafe || (saveSafe = MedianLines.SaveSkipsDisabled(World));
        private bool warned;
        private uint frame;

        private sealed class Junction
        {
            internal readonly List<Piece>[] Sides = { new List<Piece>(), new List<Piece>() };
            // The approach lines each side was built from (first road, second
            // road), then the stop bars they were cut at, in the same order.
            internal readonly Entity[] Sources = new Entity[8];
            internal readonly uint[] MissingSince = new uint[2];
            internal string Report;
        }

        private struct Piece
        {
            internal Entity Entity;
            internal Entity Prefab;
            internal Bezier4x3 Curve;
        }

        // A stretch that is not drawn, and whether a stop bar or a crosswalk
        // bounds it at its start and at its end, at which mouth.
        private struct Merged
        {
            internal float From;
            internal float To;
            internal bool StopAtFrom;
            internal bool StopAtTo;
            internal int MouthAtFrom;
            internal int MouthAtTo;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
            prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            tagged = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MedianLineMarking>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            disabled = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MedianLineMarking>(), ComponentType.ReadOnly<Disabled>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            markings = GetEntityQuery(MedianLines.MarkingQuery);
            roadPrefabs = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<SecondaryNetLane>() },
                Options = EntityQueryOptions.IncludePrefab
            });
            isStopLine = IsStopLine;
        }

        private bool IsStopLine(Entity prefab)
        {
            if (!stopLinesRead)
            {
                stopLinesRead = true;
                const SecondaryNetLaneFlags stop = SecondaryNetLaneFlags.RequireStop | SecondaryNetLaneFlags.Crossing;
                using (var roads = roadPrefabs.ToEntityArray(Allocator.Temp))
                    foreach (var road in roads)
                        foreach (var lane in EntityManager.GetBuffer<SecondaryNetLane>(road, true))
                            if ((lane.m_Flags & stop) == stop) stopLines.Add(lane.m_Lane);
            }
            if (stopLines.Contains(prefab)) return true;
            if (!stopLineNames.TryGetValue(prefab, out bool named))
            {
                named = prefabs.TryGetPrefab(new PrefabRef(prefab), out PrefabBase asset) &&
                    (asset.name == "NA Car Stop Line" || asset.name == "EU Car Stop Line");
                stopLineNames[prefab] = named;
            }
            return named;
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            // The game clears every city entity on load except disabled ones.
            if (!disabled.IsEmptyIgnoreFilter) EntityManager.RemoveComponent<Disabled>(disabled);
            lines.Clear();
            tracked.Clear();
            sweep = true;
            stopLines.Clear();
            stopLineNames.Clear();
            stopLinesRead = false;
        }

        protected override void OnUpdate()
        {
            var game = GameManager.instance;
            if (game == null || game.gameMode != GameMode.Game || game.isGameLoading) return;
            frame++;
            // A save hides the lines only while it writes the city.
            if (!disabled.IsEmptyIgnoreFilter) EntityManager.RemoveComponent<Disabled>(disabled);
            if (sweep || (frame & 255u) == 0) Sweep();
            if (!CanDraw)
            {
                if (!warned)
                {
                    warned = true;
                    Mod.Log.Warn("Median edge lines are not continued through closed junctions: this game version's save " +
                        "would keep them in the city.");
                }
                scratch.Clear();
                scratch.AddRange(lines.Keys);
                foreach (var node in scratch) Remove(node);
                return;
            }

            scratch.Clear();
            foreach (var node in lines.Keys)
                if (!medians.DrawsMedian(node)) scratch.Add(node);
            foreach (var node in scratch) Remove(node);

            pending.Clear();
            foreach (var pair in medians.ActiveJunctions)
            {
                Entity node = pair.Key;
                if (!medians.DrawsMedian(node)) continue;
                bool known = lines.TryGetValue(node, out var junction);
                if (!known)
                {
                    junction = new Junction();
                    lines[node] = junction;
                }
                if (!known || Changed(node, junction) || frame % CheckInterval == 0) pending.Add(node);
            }
            if (pending.Count > 0)
            {
                CollectMarkings();
                foreach (var node in pending) Build(node, medians.ActiveJunctions[node], lines[node]);
                ReleaseMarkings();
            }
            foreach (var pair in lines) MirrorHidden(pair.Key, pair.Value);
        }

        // Lists the visible markings owned by the closures being rebuilt and
        // by their approaches.
        private void CollectMarkings()
        {
            foreach (var node in pending)
            {
                Want(node);
                if (!EntityManager.TryGetComponent<ClosedMedian>(node, out var placement)) continue;
                Want(placement.FirstRoad);
                Want(placement.SecondRoad);
            }
            Gather(markings, owned);
        }

        private static void Gather(EntityQuery query, Dictionary<Entity, List<Entity>> into)
        {
            using (var entities = query.ToEntityArray(Allocator.Temp))
            using (var owners = query.ToComponentDataArray<Owner>(Allocator.Temp))
                for (int i = 0; i < entities.Length; i++)
                    if (into.TryGetValue(owners[i].m_Owner, out var list)) list.Add(entities[i]);
        }

        private void Want(Entity owner)
        {
            if (owner == Entity.Null || owned.ContainsKey(owner)) return;
            owned[owner] = spareLists.Count > 0 ? spareLists.Pop() : new List<Entity>();
        }

        private void ReleaseMarkings()
        {
            foreach (var list in owned.Values)
            {
                list.Clear();
                spareLists.Push(list);
            }
            owned.Clear();
        }

        private List<Entity> Owned(Entity owner) => owned.TryGetValue(owner, out var list) ? list : null;

        // Deletes lines this session does not hold, such as any that reached a
        // save another way.
        private void Sweep()
        {
            sweep = false;
            int removed = 0;
            using (var entities = tagged.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                    if (!tracked.Contains(entity))
                    {
                        EntityManager.AddComponent<Deleted>(entity);
                        removed++;
                    }
            if (removed > 0) Mod.Log.Warn($"Removed {removed} median edge line(s) that were not drawn this session, such as lines kept in a save.");
        }

        private bool Changed(Entity node, Junction junction)
        {
            if (EntityManager.HasComponent<Updated>(node)) return true;
            for (int i = 0; i < junction.Sources.Length; i++)
            {
                Entity source = junction.Sources[i];
                // A hidden stop bar (slots 4 to 7) still cuts the line.
                if (source != Entity.Null && (!EntityManager.Exists(source) || EntityManager.HasComponent<Deleted>(source) ||
                    (i < 4 && EntityManager.HasComponent<Game.Tools.Hidden>(source))))
                    return true;
            }
            foreach (var side in junction.Sides)
                for (int i = side.Count - 1; i >= 0; i--)
                {
                    Entity line = side[i].Entity;
                    if (EntityManager.Exists(line) && !EntityManager.HasComponent<Deleted>(line) &&
                        EntityManager.GetComponentData<PrefabRef>(line).m_Prefab == side[i].Prefab) continue;
                    // Gone, or given another prefab (Curbside repoints lines that use a
                    // marking definition it retires): drawn again from the approach.
                    tracked.Remove(line);
                    if (EntityManager.Exists(line) && !EntityManager.HasComponent<Deleted>(line))
                        EntityManager.AddComponent<Deleted>(line);
                    side.RemoveAt(i);
                    return true;
                }
            return false;
        }

        private void Build(Entity node, MedianGeometry median, Junction junction)
        {
            if (!EntityManager.HasComponent<ClosedMedian>(node) || !EntityManager.HasBuffer<ConnectedEdge>(node)) return;
            var placement = EntityManager.GetComponentData<ClosedMedian>(node);
            float firstSign = Sign(placement.FirstRoad, median);
            float secondSign = Sign(placement.SecondRoad, median);
            float2 firstOutward = default, secondOutward = default;
            bool roads = firstSign != 0f && secondSign != 0f && firstSign != secondSign &&
                Outward(placement.FirstRoad, node, out firstOutward) &&
                Outward(placement.SecondRoad, node, out secondOutward);
            // With diagnostics on, why each marking near the mouths was or was not taken.
            var why = MedianDiagnostics.Enabled ? new System.Text.StringBuilder() : null;
            if (roads)
            {
                why?.Append($"road {placement.FirstRoad.Index}:");
                MedianLines.FindSources(EntityManager, Owned(placement.FirstRoad), firstSign, firstOutward, median, first, hasFirst, why);
                why?.Append($" | road {placement.SecondRoad.Index}:");
                MedianLines.FindSources(EntityManager, Owned(placement.SecondRoad), secondSign, secondOutward, median, second, hasSecond, why);
                // The stop bars the lines meet, on the approach or on the junction.
                why?.Append(" | stops:");
                for (int side = 0; side < 2; side++)
                {
                    if (hasFirst[side])
                    {
                        MedianLines.FindStop(EntityManager, Owned(placement.FirstRoad), isStopLine, ref first[side], why);
                        MedianLines.FindStop(EntityManager, Owned(node), isStopLine, ref first[side], why);
                    }
                    if (hasSecond[side])
                    {
                        MedianLines.FindStop(EntityManager, Owned(placement.SecondRoad), isStopLine, ref second[side], why);
                        MedianLines.FindStop(EntityManager, Owned(node), isStopLine, ref second[side], why);
                    }
                }
            }
            var report = new System.Text.StringBuilder();
            // The junction's crosswalks, read once for both sides when a line is joined.
            bool crosswalksRead = false;
            crosswalks.Clear();
            for (int side = 0; side < 2; side++)
            {
                desired.Clear();
                junction.Sources[4 + side * 2] = Entity.Null;
                junction.Sources[4 + side * 2 + 1] = Entity.Null;
                string state;
                if (!roads) state = "the approaches could not be placed";
                else if (!hasFirst[side] || !hasSecond[side])
                    state = !hasFirst[side] && !hasSecond[side] ? "no line on either approach"
                        : $"no line on road {(hasFirst[side] ? placement.SecondRoad : placement.FirstRoad).Index}";
                else
                {
                    var curve = MedianLines.Join(first[side], second[side]);
                    // Where a line meets a stop bar, the continuation starts on the
                    // bar's junction side, leaving the gap Curbside leaves behind it.
                    float start = MedianLines.Cut(curve, first[side], true);
                    float end = MedianLines.Cut(curve, second[side], false);
                    // A painted median fills around a line the junction draws
                    // itself; a divider's edge line is left to that line.
                    if (!median.Painted && MedianLines.Overlaps(EntityManager, Owned(node), curve)) state = "a line is already drawn there";
                    else
                    {
                        // The stop-bar trims, then every marked crosswalk the line crosses.
                        cuts.Clear();
                        if (start > 0f) cuts.Add(new MedianLines.Gap { From = 0f, To = start, Stop = true, Mouth = 0 });
                        if (end < 1f) cuts.Add(new MedianLines.Gap { From = end, To = 1f, Stop = true, Mouth = 1 });
                        int trims = cuts.Count;
                        if (!crosswalksRead)
                        {
                            crosswalksRead = true;
                            why?.Append(" | crosswalks:");
                            MedianLines.FindCrosswalks(EntityManager, node, Owned(node), crosswalks, why);
                        }
                        why?.Append(side == 0 ? " | left:" : " | right:");
                        float lineHalf = math.max(LineHalfWidth(first[side].Prefab), LineHalfWidth(second[side].Prefab));
                        int crossed = MedianLines.CutAtCrosswalks(curve, crosswalks, lineHalf, cuts, why);
                        for (int i = trims; i < cuts.Count; i++)
                        {
                            var cut = cuts[i];
                            cut.Mouth = MedianLines.NearerMouth(curve, cut.From, cut.To, first[side], second[side]);
                            cuts[i] = cut;
                        }
                        int around = median.Painted ? MedianLines.CutAtLines(EntityManager, Owned(node), curve, cuts) : 0;
                        AddPieces(curve, first[side].Prefab, second[side].Prefab);
                        float drawn = 0f;
                        foreach (var piece in desired) drawn += MathUtils.Length(piece.Curve);
                        int stops = (first[side].StopBar != Entity.Null ? 1 : 0) + (second[side].StopBar != Entity.Null ? 1 : 0);
                        state = desired.Count == 0 ? "nothing left to draw between the stop bars and crosswalks" :
                            $"joined with {Name(first[side].Prefab)}" +
                            (first[side].Prefab != second[side].Prefab ? $" and {Name(second[side].Prefab)}" : "") +
                            $", {drawn:F1} m in {desired.Count} piece{(desired.Count > 1 ? "s" : "")}" +
                            (stops > 0 ? $", stopping at {stops} stop bar{(stops > 1 ? "s" : "")}" : "") +
                            (crossed > 0 ? $", cut at {crossed} crosswalk{(crossed > 1 ? "s" : "")}" : "") +
                            (around > 0 ? $", around {around} line{(around > 1 ? "s" : "")} the junction draws itself" : "");
                    }
                    junction.Sources[4 + side * 2] = first[side].StopBar;
                    junction.Sources[4 + side * 2 + 1] = second[side].StopBar;
                }
                bool sourcesMissing = roads && (!hasFirst[side] || !hasSecond[side]);
                junction.Sources[side * 2] = roads && hasFirst[side] ? first[side].Lane : Entity.Null;
                junction.Sources[side * 2 + 1] = roads && hasSecond[side] ? second[side].Lane : Entity.Null;
                if (sourcesMissing && junction.Sides[side].Count > 0)
                {
                    // Keep the line while the approach's line is drawn again.
                    if (junction.MissingSince[side] == 0) junction.MissingSince[side] = frame;
                    if (frame - junction.MissingSince[side] < GraceFrames)
                    {
                        report.Append(side == 0 ? "left " : "; right ").Append("waiting for its approach line");
                        continue;
                    }
                }
                junction.MissingSince[side] = 0;
                Apply(node, junction.Sides[side]);
                report.Append(side == 0 ? "left " : "; right ").Append(state);
            }
            string text = report.ToString();
            if (text != junction.Report && !text.Contains("waiting"))
            {
                junction.Report = text;
                Mod.Log.Info($"{(median.Painted ? "Painted median lines" : "Median edge lines")} at junction {node.Index}: {text}.");
                if (why != null) Mod.Log.Info($"DIAG lines junction {node.Index}: {why}");
            }
        }

        // The drawn pieces of the joined curve: what the cuts leave, each with
        // its approach's prefab (split at the middle when the two differ).
        private void AddPieces(Bezier4x3 curve, Entity firstPrefab, Entity secondPrefab)
        {
            cuts.Sort((x, y) => x.From.CompareTo(y.From));
            merged.Clear();
            foreach (var cut in cuts)
            {
                float from = math.saturate(cut.From), to = math.saturate(cut.To);
                if (to <= from) continue;
                int last = merged.Count - 1;
                if (last >= 0 && from <= merged[last].To)
                {
                    var stretch = merged[last];
                    if (to > stretch.To)
                    {
                        stretch.To = to;
                        stretch.StopAtTo = cut.Stop;
                        stretch.MouthAtTo = cut.Mouth;
                    }
                    merged[last] = stretch;
                }
                else merged.Add(new Merged
                {
                    From = from, To = to, StopAtFrom = cut.Stop, StopAtTo = cut.Stop,
                    MouthAtFrom = cut.Mouth, MouthAtTo = cut.Mouth
                });
            }
            for (int i = 0; i <= merged.Count; i++)
            {
                float from = i == 0 ? 0f : merged[i - 1].To;
                float to = i == merged.Count ? 1f : merged[i].From;
                if (to - from <= 0.001f) continue;
                float length = MathUtils.Length(curve, new Bounds1(from, to));
                bool afterCut = i > 0, beforeCut = i < merged.Count;
                // Between a stop bar and the crosswalk behind it, at the same mouth,
                // nothing is drawn, as at an open junction.
                bool stopToCrosswalk = afterCut && beforeCut && merged[i - 1].StopAtTo != merged[i].StopAtFrom &&
                    merged[i - 1].MouthAtTo == merged[i].MouthAtFrom;
                if (stopToCrosswalk && length <= StopToCrosswalk) continue;
                // A piece that carries an approach line on to a crosswalk is drawn
                // however short; any other only when it is long enough to show.
                if (length < (!afterCut || !beforeCut ? EndPiece : MinimumPiece)) continue;
                AddSpan(curve, from, to, firstPrefab, secondPrefab);
            }
        }

        // A piece with its approach's prefab; one that spans the middle of the
        // junction is split there between the two approaches' prefabs, unless
        // one half would be too short, which then takes the other's prefab.
        private void AddSpan(Bezier4x3 curve, float from, float to, Entity firstPrefab, Entity secondPrefab)
        {
            if (firstPrefab == secondPrefab || to <= 0.5f) AddPiece(firstPrefab, curve, from, to);
            else if (from >= 0.5f) AddPiece(secondPrefab, curve, from, to);
            else
            {
                float before = MathUtils.Length(curve, new Bounds1(from, 0.5f));
                float after = MathUtils.Length(curve, new Bounds1(0.5f, to));
                if (before >= HalfPiece && after >= HalfPiece)
                {
                    AddPiece(firstPrefab, curve, from, 0.5f);
                    AddPiece(secondPrefab, curve, 0.5f, to);
                }
                else AddPiece(before >= after ? firstPrefab : secondPrefab, curve, from, to);
            }
        }

        private void AddPiece(Entity prefab, Bezier4x3 curve, float from, float to)
        {
            desired.Add(new Piece { Prefab = prefab, Curve = MathUtils.Cut(curve, new float2(from, to)) });
        }

        // Half the painted width of a line prefab, as its mesh measures it.
        private float LineHalfWidth(Entity prefab) =>
            EntityManager.TryGetComponent<NetLaneGeometryData>(prefab, out var geometry) ? geometry.m_Size.x * 0.5f : 0f;

        private const float MinimumPiece = 1f;
        private const float EndPiece = 0.2f;
        private const float HalfPiece = 0.5f;
        // The longest stretch between a stop bar and a crosswalk; the next
        // crosswalk lies across the junction, much farther away.
        private const float StopToCrosswalk = 4f;

        private bool Outward(Entity edge, Entity node, out float2 outward) =>
            MedianLines.Outward(EntityManager, edge, node, out outward);

        // The direction of a road from the junction along the median's axis.
        private float Sign(Entity edge, MedianGeometry median)
        {
            if (!EntityManager.HasComponent<Curve>(edge)) return 0f;
            float x = median.Local(MathUtils.Position(EntityManager.GetComponentData<Curve>(edge).m_Bezier, 0.5f)).x;
            return math.abs(x) < 0.5f ? 0f : math.sign(x);
        }

        // Makes a side's drawn pieces match the desired ones, reusing the
        // entities where the prefabs match.
        private void Apply(Entity node, List<Piece> existing)
        {
            bool reuse = existing.Count == desired.Count;
            for (int i = 0; reuse && i < existing.Count; i++)
                reuse = existing[i].Prefab == desired[i].Prefab;
            if (reuse)
            {
                for (int i = 0; i < existing.Count; i++)
                {
                    if (MedianLines.Close(existing[i].Curve, desired[i].Curve)) continue;
                    var piece = existing[i];
                    piece.Curve = desired[i].Curve;
                    existing[i] = piece;
                    EntityManager.SetComponentData(piece.Entity, new Curve { m_Bezier = piece.Curve, m_Length = MathUtils.Length(piece.Curve) });
                    EntityManager.AddComponent<Updated>(piece.Entity);
                }
                return;
            }
            Delete(existing);
            bool hidden = EntityManager.HasComponent<Game.Tools.Hidden>(node);
            foreach (var piece in desired)
                existing.Add(new Piece { Entity = Create(piece.Prefab, piece.Curve, hidden), Prefab = piece.Prefab, Curve = piece.Curve });
        }

        // A marking lane built the way the game builds one, without an owner.
        private Entity Create(Entity prefab, Bezier4x3 curve, bool hidden)
        {
            var archetype = EntityManager.GetComponentData<NetLaneArchetypeData>(prefab).m_LaneArchetype;
            Entity line = EntityManager.CreateEntity(archetype);
            EntityManager.SetComponentData(line, new PrefabRef(prefab));
            EntityManager.SetComponentData(line, new Lane
            {
                m_StartNode = new PathNode(new PathNode(line, (ushort)0), true),
                m_MiddleNode = new PathNode(new PathNode(line, (ushort)1), true),
                m_EndNode = new PathNode(new PathNode(line, (ushort)2), true)
            });
            EntityManager.SetComponentData(line, new Curve { m_Bezier = curve, m_Length = MathUtils.Length(curve) });
            // Without it the game seats the curve on the terrain.
            EntityManager.AddComponentData(line, default(Game.Net.Elevation));
            // Keeps the line out of the game's lane overlap checks, as for its own markings.
            if (!EntityManager.HasComponent<Game.Net.SecondaryLane>(line)) EntityManager.AddComponent<Game.Net.SecondaryLane>(line);
            EntityManager.AddComponent<MedianLineMarking>(line);
            if (!EntityManager.HasComponent<Created>(line)) EntityManager.AddComponent<Created>(line);
            if (!EntityManager.HasComponent<Updated>(line)) EntityManager.AddComponent<Updated>(line);
            if (hidden) EntityManager.AddComponent<Game.Tools.Hidden>(line);
            tracked.Add(line);
            return line;
        }

        private void Delete(List<Piece> pieces)
        {
            foreach (var piece in pieces)
            {
                tracked.Remove(piece.Entity);
                if (EntityManager.Exists(piece.Entity) && !EntityManager.HasComponent<Deleted>(piece.Entity))
                    EntityManager.AddComponent<Deleted>(piece.Entity);
            }
            pieces.Clear();
        }

        // The game hides a junction's or road's lanes while a tool previews a
        // change to it; it does not know these lines, so they follow the
        // junction and its two approaches here.
        private void MirrorHidden(Entity node, Junction junction)
        {
            bool hidden = EntityManager.HasComponent<Game.Tools.Hidden>(node);
            if (!hidden && EntityManager.TryGetComponent<ClosedMedian>(node, out var placement))
                hidden = EntityManager.HasComponent<Game.Tools.Hidden>(placement.FirstRoad) ||
                    EntityManager.HasComponent<Game.Tools.Hidden>(placement.SecondRoad);
            foreach (var side in junction.Sides)
                foreach (var piece in side)
                {
                    if (!EntityManager.Exists(piece.Entity) || EntityManager.HasComponent<Game.Tools.Hidden>(piece.Entity) == hidden) continue;
                    if (hidden) EntityManager.AddComponent<Game.Tools.Hidden>(piece.Entity);
                    else EntityManager.RemoveComponent<Game.Tools.Hidden>(piece.Entity);
                    EntityManager.AddComponent<BatchesUpdated>(piece.Entity);
                }
        }

        private void Remove(Entity node)
        {
            if (!lines.TryGetValue(node, out var junction)) return;
            foreach (var side in junction.Sides) Delete(side);
            lines.Remove(node);
            if (junction.Report != null) Mod.Log.Info($"Median edge lines at junction {node.Index} removed.");
        }

        private string Name(Entity prefab) =>
            prefabs.TryGetPrefab(new PrefabRef(prefab), out PrefabBase asset) ? asset.name : prefab.Index.ToString();
    }

    // Keeps the continued lines out of every save: they are disabled while
    // the game writes the city, which leaves out disabled entities, and
    // enabled again right after. Nothing else runs in between, so they never
    // flicker, and a city saved with them loads and uninstalls cleanly.
    public sealed partial class MedianLineSaveHide : GameSystemBase
    {
        private EntityQuery lines;

        protected override void OnCreate()
        {
            base.OnCreate();
            lines = GetEntityQuery(ComponentType.ReadOnly<MedianLineMarking>());
        }

        protected override void OnUpdate()
        {
            if (lines.IsEmptyIgnoreFilter) return;
            Mod.Log.Info($"Saving: {lines.CalculateEntityCount()} median edge line(s) left out of the save.");
            EntityManager.AddComponent<Disabled>(lines);
        }
    }

    public sealed partial class MedianLineSaveRestore : GameSystemBase
    {
        private EntityQuery lines;

        protected override void OnCreate()
        {
            base.OnCreate();
            lines = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MedianLineMarking>(), ComponentType.ReadOnly<Disabled>() },
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
        }

        protected override void OnUpdate()
        {
            if (!lines.IsEmptyIgnoreFilter) EntityManager.RemoveComponent<Disabled>(lines);
        }
    }
}
