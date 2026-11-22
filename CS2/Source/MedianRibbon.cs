using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    internal readonly struct MedianMouth
    {
        internal readonly float3 Left;
        internal readonly float3 Right;
        internal readonly float3 Tangent;

        internal MedianMouth(float3 left, float3 right, float3 tangent)
        {
            Left = left;
            Right = right;
            Tangent = tangent;
        }

        internal float3 Center => (Left + Right) * 0.5f;
    }

    internal readonly struct MedianSection
    {
        internal readonly float Time;
        internal readonly float3 Center;
        internal readonly float3 Across;
        internal readonly float2 Direction;
        internal readonly float Curvature;

        internal MedianSection(float time, float3 center, float3 across, float2 direction, float curvature)
        {
            Time = time;
            Center = center;
            Across = across;
            Direction = direction;
            Curvature = curvature;
        }

        internal float3 Left => Center + Across;
        internal float3 Right => Center - Across;
    }

    internal readonly struct MedianRibbon
    {
        internal const float JoinTolerance = 0.06f;
        internal const float SeamSkewTolerance = 0.25f;
        internal const float SamplingError = 0.0125f;
        internal const float WidthTolerance = 0.12f;
        internal readonly Bezier4x3 Center;
        internal readonly float HalfWidth;
        private readonly float2 crossfall;
        private readonly float side;

        internal MedianRibbon(Bezier4x3 center, MedianMouth start, MedianMouth end, float halfWidth)
        {
            Center = center;
            HalfWidth = halfWidth;
            side = math.dot(MathUtils.Left(start.Tangent.xz), (start.Left - start.Right).xz) < 0f ? -1f : 1f;
            crossfall = new float2(start.Left.y - start.Right.y, end.Left.y - end.Right.y) * 0.5f;
        }

        internal static Bezier4x3 FitCenter(MedianMouth start, MedianMouth end, Bezier4x3 guide)
        {
            float3 a = start.Center;
            float3 d = end.Center;
            float length = math.distance(a.xz, d.xz);
            var forward = math.normalizesafe((d - a).xz);
            float3 from = AlongGrade(start.Tangent, forward);
            float3 to = AlongGrade(end.Tangent, forward);
            float3 startShift = a - guide.a;
            float3 endShift = d - guide.d;
            var fixedEnds = new Bezier4x3(a, a, d, d);
            float aa = 0f, ab = 0f, bb = 0f, ax = 0f, bx = 0f;
            for (int i = 1; i < 8; i++)
            {
                float t = i / 8f;
                float u = 1f - t;
                float2 first = from.xz * (3f * u * u * t);
                float2 second = -to.xz * (3f * u * t * t);
                float2 residual = MathUtils.Position(guide, t).xz + math.lerp(startShift.xz, endShift.xz, t) -
                    MathUtils.Position(fixedEnds, t).xz;
                aa += math.dot(first, first);
                ab += math.dot(first, second);
                bb += math.dot(second, second);
                ax += math.dot(first, residual);
                bx += math.dot(second, residual);
            }
            float determinant = aa * bb - ab * ab;
            float2 handles = determinant > 0.00001f
                ? new float2(bb * ax - ab * bx, aa * bx - ab * ax) / determinant : length / 3f;
            handles = math.clamp(handles, length * 0.18f, length * 0.48f);
            return new Bezier4x3(a, a + from * handles.x, d - to * handles.y, d);
        }

        private static float3 AlongGrade(float3 tangent, float2 fallback)
        {
            float length = math.length(tangent.xz);
            return length > 0.0001f ? tangent / length : new float3(fallback.x, 0f, fallback.y);
        }

        internal MedianSection Section(float time)
        {
            float3 tangent = MathUtils.Tangent(Center, time);
            float2 velocity = tangent.xz;
            float speed = math.length(velocity);
            float2 acceleration = MathUtils.Acceleration(Center, time).xz;
            float curvature = speed > 0.0001f ? Cross(velocity, acceleration) / (speed * speed * speed) : float.NaN;
            float2 direction = math.normalizesafe(velocity);
            float2 across = MathUtils.Left(direction) * (side * HalfWidth);
            float3 offset = new float3(across.x, math.lerp(crossfall.x, crossfall.y, time), across.y);
            return new MedianSection(time, MathUtils.Position(Center, time), offset, direction, curvature);
        }

        // Point at a signed distance across the ribbon, positive toward its left
        // curb. The height follows the road's crossfall at both mouths.
        internal float3 Offset(float time, float lateral)
        {
            var section = Section(time);
            return section.Center + section.Across * (lateral / HalfWidth);
        }

        internal float3 OffsetTangent(float time, float lateral)
        {
            float3 tangent = MathUtils.Tangent(Center, time);
            float2 direction = math.normalizesafe(tangent.xz);
            float2 acceleration = MathUtils.Acceleration(Center, time).xz;
            float2 directionRate = (acceleration - direction * math.dot(acceleration, direction)) /
                math.max(0.0001f, math.length(tangent.xz));
            float2 acrossRate = MathUtils.Left(directionRate) * (side * lateral);
            return tangent + new float3(acrossRate.x, (crossfall.y - crossfall.x) * (lateral / HalfWidth), acrossRate.y);
        }

        // One curve along the whole ribbon at a signed distance across it.
        internal Bezier4x3 Rail(float lateral)
        {
            float3 a = Offset(0f, lateral);
            float3 d = Offset(1f, lateral);
            return new Bezier4x3(a, a + OffsetTangent(0f, lateral) / 3f, d - OffsetTangent(1f, lateral) / 3f, d);
        }

        // Ribbon coordinates of a point: the time of the nearest center point and
        // the signed distance across. False when the point lies past either mouth.
        internal bool Locate(float3 point, out float time, out float lateral)
        {
            float best = float.MaxValue;
            time = 0f;
            for (int i = 0; i <= 32; i++)
            {
                float sample = i / 32f;
                float distance = math.distancesq(MathUtils.Position(Center, sample).xz, point.xz);
                if (distance >= best) continue;
                best = distance;
                time = sample;
            }
            for (int i = 0; i < 4; i++)
            {
                float2 tangent = MathUtils.Tangent(Center, time).xz;
                float speed = math.lengthsq(tangent);
                if (speed < 0.000001f) break;
                time += math.dot(point.xz - MathUtils.Position(Center, time).xz, tangent) / speed;
            }
            bool inside = time >= 0f && time <= 1f;
            time = math.saturate(time);
            lateral = math.dot(point.xz - MathUtils.Position(Center, time).xz, Normal(time).xz);
            return inside;
        }

        // Unit direction across the ribbon toward its left curb.
        internal float3 Normal(float time)
        {
            float2 across = MathUtils.Left(math.normalizesafe(MathUtils.Tangent(Center, time).xz)) * side;
            return new float3(across.x, 0f, across.y);
        }

        // The rigid divider and the deformed pavement are cut into straight
        // segments at different stations. Along a curve their edges can differ
        // by the sagitta of a segment spanning this share of the ribbon.
        internal float ChordError(float share)
        {
            float curvature = 0f;
            for (int i = 0; i <= 8; i++)
            {
                float sample = math.abs(Section(i / 8f).Curvature);
                if (math.isfinite(sample)) curvature = math.max(curvature, sample);
            }
            float chord = MathUtils.Length(Center) * share;
            return curvature * chord * chord / 8f;
        }

        internal float3 CurbTangent(float time, bool left)
        {
            float3 tangent = MathUtils.Tangent(Center, time);
            float2 direction = math.normalizesafe(tangent.xz);
            float2 acceleration = MathUtils.Acceleration(Center, time).xz;
            float2 directionRate = (acceleration - direction * math.dot(acceleration, direction)) /
                math.max(0.0001f, math.length(tangent.xz));
            float2 acrossRate = MathUtils.Left(directionRate) * (side * HalfWidth);
            var offsetRate = new float3(acrossRate.x, crossfall.y - crossfall.x, acrossRate.y);
            return left ? tangent + offsetRate : tangent - offsetRate;
        }

        internal bool Sample(MedianMouth start, MedianMouth end, List<MedianSection> sections, out string reason)
        {
            sections.Clear();
            var a = Section(0f);
            var b = Section(1f);
            if (!Matches(a, start) || !Matches(b, end))
            {
                reason = $"the offset curbs do not meet the native road ({JoinError(a, start)} at the first join, " +
                    $"{JoinError(b, end)} at the second)";
                return false;
            }
            sections.Add(a);
            if (!Subdivide(a, b, sections, 0) || !ValidStrip(sections))
            {
                reason = "the median cross-sections fold or intersect";
                return false;
            }
            reason = null;
            return true;
        }

        private static bool Matches(MedianSection section, MedianMouth mouth)
        {
            return MatchesCurb(section, section.Left - mouth.Left) && MatchesCurb(section, section.Right - mouth.Right) &&
                math.dot(section.Direction, math.normalizesafe(mouth.Tangent.xz)) >= 0.999f;
        }

        private static bool MatchesCurb(MedianSection section, float3 error)
        {
            // The native mesh may cut each curb at a slightly different station.
            // Retain those vertices and measure the width across the road tangent.
            float2 normal = MathUtils.Left(section.Direction);
            return math.abs(math.dot(error.xz, normal)) <= JoinTolerance &&
                math.abs(error.y) <= JoinTolerance &&
                math.abs(math.dot(error.xz, section.Direction)) <= SeamSkewTolerance;
        }

        private static string JoinError(MedianSection section, MedianMouth mouth)
        {
            float2 offset = (section.Left - mouth.Left).xz;
            return $"{math.abs(Cross(section.Direction, offset)):F3} m lateral / " +
                $"{math.abs(math.dot(section.Direction, offset)):F3} m along the road";
        }

        private bool Subdivide(MedianSection a, MedianSection b, List<MedianSection> sections, int depth)
        {
            var middle = Section((a.Time + b.Time) * 0.5f);
            if (!ValidCurvature(a) || !ValidCurvature(middle) || !ValidCurvature(b)) return false;
            float error = math.max(math.distance(middle.Left.xz, (a.Left.xz + b.Left.xz) * 0.5f),
                math.distance(middle.Right.xz, (a.Right.xz + b.Right.xz) * 0.5f));
            if (error <= SamplingError && math.distancesq(a.Center.xz, b.Center.xz) <= 1f &&
                math.dot(a.Direction, b.Direction) >= 0.9986f)
            {
                sections.Add(b);
                return true;
            }
            return depth < 8 && Subdivide(a, middle, sections, depth + 1) && Subdivide(middle, b, sections, depth + 1);
        }

        private bool ValidCurvature(MedianSection section) =>
            math.isfinite(section.Curvature) && math.abs(section.Curvature) * HalfWidth < 0.9f;

        internal static bool ValidStrip(List<MedianSection> sections)
        {
            if (sections.Count < 2) return false;
            float winding = math.sign(Cross(sections[0].Direction, sections[0].Across.xz));
            if (winding == 0f) return false;
            for (int i = 1; i < sections.Count; i++)
            {
                var a = sections[i - 1];
                var b = sections[i];
                if (!math.all(math.isfinite(a.Left)) || !math.all(math.isfinite(a.Right)) ||
                    !math.all(math.isfinite(b.Left)) || !math.all(math.isfinite(b.Right))) return false;
                if (Cross(b.Left.xz - a.Left.xz, a.Right.xz - a.Left.xz) * winding >= -0.00001f ||
                    Cross(b.Right.xz - a.Right.xz, b.Left.xz - a.Right.xz) * winding <= 0.00001f) return false;
                for (int j = 1; j < i; j++)
                {
                    var c = sections[j - 1];
                    var d = sections[j];
                    if (Intersects(a.Left.xz, b.Left.xz, c.Right.xz, d.Right.xz) ||
                        Intersects(a.Right.xz, b.Right.xz, c.Left.xz, d.Left.xz)) return false;
                    if (j + 1 < i && (Intersects(a.Left.xz, b.Left.xz, c.Left.xz, d.Left.xz) ||
                        Intersects(a.Right.xz, b.Right.xz, c.Right.xz, d.Right.xz))) return false;
                }
                var start = sections[0];
                var end = sections[sections.Count - 1];
                if (i > 1 && (Intersects(start.Left.xz, start.Right.xz, a.Left.xz, b.Left.xz) ||
                    Intersects(start.Left.xz, start.Right.xz, a.Right.xz, b.Right.xz))) return false;
                if (i + 1 < sections.Count && (Intersects(end.Left.xz, end.Right.xz, a.Left.xz, b.Left.xz) ||
                    Intersects(end.Left.xz, end.Right.xz, a.Right.xz, b.Right.xz))) return false;
            }
            return true;
        }

        internal static bool ClearsLane(List<MedianSection> sections, Bezier4x3 lane, float halfLaneWidth) =>
            MinimumClearance(sections, lane) >= math.max(0f, halfLaneWidth - JoinTolerance);

        internal static float MinimumClearance(List<MedianSection> sections, Bezier4x3 lane)
        {
            if (Contains(sections, lane.a.xz)) return 0f;
            return math.sqrt(LaneDistanceSq(sections, lane.xz, 0));
        }

        private static float LaneDistanceSq(List<MedianSection> sections, Bezier4x2 lane, int depth)
        {
            // A Bezier lies in its control hull. Split whenever that hull is
            // farther from the chord than the sampling tolerance.
            const float laneError = SamplingError * 0.5f;
            if (math.max(PointDistanceSq(lane.b, lane.a, lane.d), PointDistanceSq(lane.c, lane.a, lane.d)) >
                laneError * laneError)
            {
                if (depth >= 9) return 0f;
                MathUtils.Divide(lane, out var a, out var b, 0.5f);
                return math.min(LaneDistanceSq(sections, a, depth + 1), LaneDistanceSq(sections, b, depth + 1));
            }

            var start = sections[0];
            var end = sections[sections.Count - 1];
            float clearance = math.min(SegmentDistanceSq(lane.a, lane.d, start.Left.xz, start.Right.xz),
                SegmentDistanceSq(lane.a, lane.d, end.Left.xz, end.Right.xz));
            for (int i = 1; i < sections.Count && clearance > 0f; i++)
            {
                var a = sections[i - 1];
                var b = sections[i];
                clearance = math.min(clearance, SegmentDistanceSq(lane.a, lane.d, a.Left.xz, b.Left.xz));
                clearance = math.min(clearance, SegmentDistanceSq(lane.a, lane.d, a.Right.xz, b.Right.xz));
            }
            return clearance;
        }

        private static bool Contains(List<MedianSection> sections, float2 point)
        {
            bool inside = false;
            int last = sections.Count - 1;
            inside ^= RayCrosses(point, sections[0].Left.xz, sections[0].Right.xz);
            inside ^= RayCrosses(point, sections[last].Left.xz, sections[last].Right.xz);
            for (int i = 1; i <= last; i++)
            {
                inside ^= RayCrosses(point, sections[i - 1].Left.xz, sections[i].Left.xz);
                inside ^= RayCrosses(point, sections[i - 1].Right.xz, sections[i].Right.xz);
            }
            return inside;
        }

        private static bool RayCrosses(float2 point, float2 a, float2 b)
        {
            return (a.y > point.y) != (b.y > point.y) &&
                point.x < math.lerp(a.x, b.x, (point.y - a.y) / (b.y - a.y));
        }

        private static float SegmentDistanceSq(float2 a, float2 b, float2 c, float2 d)
        {
            if (Intersects(a, b, c, d)) return 0f;
            return math.cmin(new float4(PointDistanceSq(a, c, d), PointDistanceSq(b, c, d),
                PointDistanceSq(c, a, b), PointDistanceSq(d, a, b)));
        }

        private static float PointDistanceSq(float2 point, float2 a, float2 b)
        {
            float2 delta = b - a;
            float fraction = math.saturate(math.dot(point - a, delta) / math.max(0.00000001f, math.lengthsq(delta)));
            return math.distancesq(point, a + delta * fraction);
        }

        private static bool Intersects(float2 a, float2 b, float2 c, float2 d)
        {
            if (math.any(math.max(a, b) < math.min(c, d)) || math.any(math.max(c, d) < math.min(a, b))) return false;
            float2 ab = b - a;
            float2 cd = d - c;
            return Cross(ab, c - a) * Cross(ab, d - a) <= 0f && Cross(cd, a - c) * Cross(cd, b - c) <= 0f;
        }

        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
    }
}
