using Colossal.Mathematics;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    internal readonly struct MedianRenderRails
    {
        private readonly Bezier4x3 left;
        private readonly Bezier4x3 right;

        internal MedianRenderRails(MedianRibbon ribbon, bool far)
        {
            float start = far ? 1f : 0f;
            float direction = far ? -0.5f : 0.5f;
            var mouth = ribbon.Section(start);
            var seam = ribbon.Section(0.5f);
            left = new Bezier4x3(
                far ? mouth.Right : mouth.Left,
                (far ? mouth.Right : mouth.Left) + ribbon.CurbTangent(start, !far) * (direction / 3f),
                (far ? seam.Right : seam.Left) - ribbon.CurbTangent(0.5f, !far) * (direction / 3f),
                far ? seam.Right : seam.Left);
            right = new Bezier4x3(
                far ? mouth.Left : mouth.Right,
                (far ? mouth.Left : mouth.Right) + ribbon.CurbTangent(start, far) * (direction / 3f),
                (far ? seam.Left : seam.Right) - ribbon.CurbTangent(0.5f, far) * (direction / 3f),
                far ? seam.Left : seam.Right);
        }

        // Rails given directly. The left rail lies on the left of the direction
        // in which both curves run.
        internal MedianRenderRails(Bezier4x3 left, Bezier4x3 right)
        {
            this.left = left;
            this.right = right;
        }

        internal void Sample(float time, out float3 a, out float3 b)
        {
            a = MathUtils.Position(left, time);
            b = MathUtils.Position(right, time);
        }

        internal void Pack(float width, float lateralOffset, float halfMeshLength, float halfCurbWidth,
            float3 origin, float4 station, out float4x4 a, out float4x4 b)
        {
            // The mesh uses road-normalized x. Along the road the game maps the
            // kept half of the piece to (L - z) / W, so a tile shorter than the
            // road width occupies only the start of the shape curves, from the
            // mouth. Extend our two rails past the seam to that coordinate domain.
            float scale = 2f * halfCurbWidth;
            float leftAcross = 0.5f - (width * 0.5f + lateralOffset) / scale;
            float rightAcross = leftAcross + width / scale;
            float extension = width / halfMeshLength;
            a = Extend(PackRail(MathUtils.Lerp(left, right, leftAcross), origin, station), extension);
            b = Extend(PackRail(MathUtils.Lerp(left, right, rightAcross), origin, station), extension);
        }

        private static float4x4 PackRail(Bezier4x3 curve, float3 origin, float4 station) => new float4x4(
            new float4(curve.a - origin, station.x), new float4(curve.b - origin, station.y),
            new float4(curve.c - origin, station.z), new float4(curve.d - origin, station.w));

        // Reparameterize so the result at u equals the curve at u * ratio. The
        // mouth stays at parameter zero and the seam moves to 1 / ratio.
        private static float4x4 Extend(float4x4 curve, float ratio)
        {
            float4 start = curve.c0;
            float4 first = math.lerp(start, curve.c1, ratio);
            float4 next = math.lerp(curve.c1, curve.c2, ratio);
            float4 second = math.lerp(first, next, ratio);
            float4 third = math.lerp(next, math.lerp(curve.c2, curve.c3, ratio), ratio);
            return new float4x4(start, first, second, math.lerp(second, third, ratio));
        }
    }
}
