using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    // Saved on the junction entity, whose identity is remapped by the game's serializer.
    public struct MedianPlacement : IComponentData, ISerializable
    {
        public Entity FirstRoad;
        public Entity SecondRoad;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(FirstRoad);
            writer.Write(SecondRoad);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out FirstRoad);
            reader.Read(out SecondRoad);
        }
    }

    public struct ClosedMedian : IComponentData, ISerializable
    {
        public Entity FirstRoad;
        public Entity SecondRoad;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(FirstRoad);
            writer.Write(SecondRoad);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out FirstRoad);
            reader.Read(out SecondRoad);
        }
    }

    internal struct MedianGeometry
    {
        // A painted median (a center line or a painted strip between the two
        // directions): nothing is built; its paint is continued through the
        // junction and the turns across it are cut. Not saved: read from the
        // roads each time, so a closure keeps the same save format.
        internal bool Painted;
        internal float HalfWidth;
        internal const float HalfHeight = 2f;
        internal float3 Center;
        internal float2 Axis;
        internal float HalfLength;

        internal float3 Local(float3 point)
        {
            float3 relative = point - Center;
            return new float3(math.dot(relative.xz, Axis), relative.y,
                math.dot(relative.xz, new float2(-Axis.y, Axis.x)));
        }

        internal bool Near(float3 point, float margin)
        {
            return math.all(math.abs(Local(point)) <= new float3(
                HalfLength + margin, HalfHeight + margin, HalfWidth + margin));
        }

        internal bool Hits(float3 begin, float3 end)
        {
            return HitsLocal(Local(begin), Local(end));
        }

        private bool HitsLocal(float3 a, float3 b)
        {
            float start = 0f;
            float end = 1f;
            return Clip(a.x, b.x, HalfLength, ref start, ref end)
                && Clip(a.y, b.y, HalfHeight, ref start, ref end)
                && Clip(a.z, b.z, HalfWidth, ref start, ref end);
        }

        private static bool Clip(float a, float b, float extent, ref float start, ref float end)
        {
            float delta = b - a;
            if (math.abs(delta) < 0.00001f) return math.abs(a) <= extent;
            float enter = (-extent - a) / delta;
            float exit = (extent - a) / delta;
            start = math.max(start, math.min(enter, exit));
            end = math.min(end, math.max(enter, exit));
            return start <= end;
        }

        internal bool Hits(Bezier4x3 curve)
        {
            return HitsCurve(Local(curve.a), Local(curve.b), Local(curve.c), Local(curve.d),
                new float3(HalfLength, HalfHeight, HalfWidth), 0);
        }

        // Half width of the band around the line between the two median ends
        // that a lane must enter to count as crossing the closed median.
        internal const float GapHalfWidth = 0.1f;

        // Whether a lane crosses the closed median: it reaches the line between
        // the two median ends inside the gap. A lane that only comes within the
        // divider's width of that line, such as a through lane where the
        // approaches are offset, does not.
        internal bool CrossesGap(Bezier4x3 curve)
        {
            return HitsCurve(Local(curve.a), Local(curve.b), Local(curve.c), Local(curve.d),
                new float3(HalfLength, HalfHeight, GapHalfWidth), 0);
        }

        private static bool HitsCurve(float3 a, float3 b, float3 c, float3 d, float3 extent, int depth)
        {
            // Convex hull rejection ensures a tight turn cannot slip between samples.
            float3 low = math.min(math.min(a, b), math.min(c, d));
            float3 high = math.max(math.max(a, b), math.max(c, d));
            if (math.any(low > extent) || math.any(high < -extent)) return false;
            if (depth == 8 || math.all(math.abs(a) <= extent) || math.all(math.abs(d) <= extent)) return true;

            float3 ab = (a + b) * 0.5f;
            float3 bc = (b + c) * 0.5f;
            float3 cd = (c + d) * 0.5f;
            float3 abc = (ab + bc) * 0.5f;
            float3 bcd = (bc + cd) * 0.5f;
            float3 middle = (abc + bcd) * 0.5f;
            return HitsCurve(a, ab, abc, middle, extent, depth + 1) ||
                HitsCurve(middle, bcd, cd, d, extent, depth + 1);
        }
    }
}
