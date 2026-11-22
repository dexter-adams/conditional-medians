using Colossal.Mathematics;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    internal static class MedianTexturePhase
    {
        // Largest change of the texture's scale along the junction that is
        // accepted to keep the pattern continuous at the second mouth.
        internal const float MaximumStretch = 0.05f;

        // "forward" is set when the first road ends at the junction, so its
        // distance keeps rising across it. "continuous" is set when the second
        // road's distance runs the same way, which holds when exactly one of the
        // two roads starts at the junction. Two roads that both start or both end
        // there mirror each other at the second mouth, and no phase joins them.
        internal static void Map(Bezier4x3 center, float nearMouth, float farMouth, float period,
            bool forward, bool continuous, out float4 first, out float4 second)
        {
            MathUtils.Divide(center, out var near, out var far, 0.5f);
            far = MathUtils.Invert(far);
            float nearLength = MathUtils.Length(near);
            float farLength = MathUtils.Length(far);
            float length = nearLength + farLength;
            float expectedEnd = nearMouth + (forward ? length : -length);
            // The next native segment already has a phase. Reach its nearest
            // repeat when a small stretch across both node halves absorbs the
            // difference. One repeat of a stock road spans many curb stones, so
            // a larger difference keeps the native scale. The pattern then
            // restarts at the second mouth, as it does at every native junction.
            float matched = farMouth + math.round((expectedEnd - farMouth) / period) * period;
            float end = continuous && math.abs(matched - expectedEnd) <= MaximumStretch * length
                ? matched : expectedEnd;
            float scale = (end - nearMouth) / math.max(0.001f, length);
            float seam = nearMouth + scale * nearLength;
            first = Mapping(near, nearMouth, seam, scale);
            second = Mapping(far, end, seam, -scale);
        }

        internal static float4 Compensate(float4 station, float4 outer, float4 weights)
        {
            // Match the blended UV's value and slope at the mouth and seam.
            float4 complement = 1f - weights;
            var inner = (station - weights * outer) / complement;
            inner.y = inner.x + ((station.y - station.x) - weights.x * (outer.y - outer.x) -
                (weights.y - weights.x) * (outer.x - inner.x)) / complement.x;
            inner.z = inner.w - ((station.w - station.z) - weights.w * (outer.w - outer.z) -
                (weights.w - weights.z) * (outer.w - inner.w)) / complement.w;
            return inner;
        }

        private static float4 Mapping(Bezier4x3 curve, float mouth, float seam, float scale) => new float4(
            mouth, mouth + scale * math.length(curve.b - curve.a),
            seam - scale * math.length(curve.d - curve.c), seam);
    }
}
