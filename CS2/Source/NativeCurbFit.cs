using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    // The native node meshes blend their outer rails toward the center of the junction.
    // Fit the rails as a pair so a curb's deformation cannot widen the ribbon in isolation.
    internal readonly struct NativeCurbFit
    {
        // The node shader's longitudinal blend is approximated from the native
        // curb profile. The source rails keep the same mouth and seam stations.
        private const float SyncEase = 0.75f;
        internal readonly Bezier4x3 Inner;
        private readonly Bezier4x3 outer;
        private readonly float fraction;
        internal readonly float SeamFraction;

        private NativeCurbFit(Bezier4x3 inner, Bezier4x3 outer, float fraction, float seamFraction)
        {
            Inner = inner;
            this.outer = outer;
            this.fraction = fraction;
            SeamFraction = seamFraction;
        }

        internal float3 Curb(float time)
        {
            return math.lerp(MathUtils.Position(Inner, time), MathUtils.Position(outer, time), Weight(time));
        }

        private float Weight(float time) =>
            math.lerp(SeamFraction, fraction, (1f - time) * (1f + SyncEase * time));

        // The node mesh scales linearly between the inner rail and its first
        // sync marker. A marker at the outer edge of the divider piece therefore
        // leaves the curb on this fit and also carries the adjoining pavement,
        // which starts at that edge, at the piece's own distance from the curb.
        internal float MarkerSeamFraction(float scale) => math.min(1f, SeamFraction * scale);

        internal float3 Marker(float time, float scale) =>
            math.lerp(MathUtils.Position(Inner, time), MathUtils.Position(outer, time),
                math.min(1f, Weight(time) * scale));

        // Move the inner rail toward the far side of the median. Everything the
        // node mesh deforms between this rail and the outer rail follows, the
        // pavement edge beside the divider by almost the full distance. Use it
        // only while the divider is drawn on its own rails.
        //
        // The rail's start stays on the native mouth point. The node mesh then
        // still meets the road mesh vertex for vertex at the mouth, and a later
        // fit that reads this rail back starts from the same point and returns
        // the same result. The move ramps in along the half as Ramp describes.
        internal NativeCurbFit Tucked(MedianRibbon ribbon, bool far, bool left, float distance)
        {
            float shift = left ? -distance : distance;
            var curve = Inner;
            curve.b += ribbon.Normal(far ? 5f / 6f : 1f / 6f) * shift;
            curve.c += ribbon.Normal(far ? 2f / 3f : 1f / 3f) * shift;
            curve.d += ribbon.Normal(0.5f) * shift;
            return new NativeCurbFit(curve, outer, fraction, SeamFraction);
        }

        // Share of the tuck that the inner rail has taken up at a time along its half.
        internal static float Ramp(float time)
        {
            float rest = 1f - math.saturate(time);
            return 1f - rest * rest * rest;
        }

        internal const float MinimumTuck = 0.01f;
        internal const float MaximumTuck = 0.08f;

        // Distance to move the inner rails so the pavement sits under a divider
        // that is drawn on its own rails. The pavement edge needs a margin, the
        // difference between the two sets of straight segments on a curve, and
        // whatever gap the fit still models where the tuck has ramped in. It
        // follows the inner rail by the share of the way that its marker leaves
        // to the outer rail.
        internal static float SkirtTuck(MedianRibbon ribbon, List<MedianSection> sections,
            NativeCurbFit aLeft, NativeCurbFit aRight, NativeCurbFit bLeft, NativeCurbFit bRight,
            float aScale, float bScale)
        {
            float skirt = ribbon.HalfWidth * (math.min(aScale, bScale) - 1f);
            if (skirt <= 0f) return 0f;
            float gap = 0f;
            foreach (var section in sections)
            {
                bool first = section.Time <= 0.5f;
                float time = (first ? section.Time : 1f - section.Time) * 2f;
                float ramp = Ramp(time);
                if (ramp < 0.05f) continue;
                gap = math.max(gap, math.cmax(SkirtGap(ribbon, section, first ? aLeft : bLeft,
                    first ? aRight : bRight, time, first ? aScale : bScale)) / ramp);
            }
            float weight = math.max(math.max(aLeft.MarkerWeight(aScale), aRight.MarkerWeight(aScale)),
                math.max(bLeft.MarkerWeight(bScale), bRight.MarkerWeight(bScale)));
            float reach = 1f - math.min(0.5f, weight);
            return math.min(math.min(MaximumTuck, skirt * 0.5f),
                (MinimumTuck + 2f * ribbon.ChordError(0.125f) + gap) / reach);
        }

        // Largest share of the way from the inner rail to the outer rail at
        // which the marker sits along this half.
        private float MarkerWeight(float scale) => math.max(fraction, SeamFraction) * scale;

        // Positive values put the modelled pavement edge outside the divider's
        // skirt edge, which would open a gap. Negative values tuck it under.
        internal static float2 SkirtRange(MedianRibbon ribbon, List<MedianSection> sections,
            NativeCurbFit aLeft, NativeCurbFit aRight, NativeCurbFit bLeft, NativeCurbFit bRight,
            float aScale, float bScale)
        {
            float2 range = new float2(float.PositiveInfinity, float.NegativeInfinity);
            foreach (var section in sections)
            {
                bool first = section.Time <= 0.5f;
                float time = (first ? section.Time : 1f - section.Time) * 2f;
                float2 gap = SkirtGap(ribbon, section, first ? aLeft : bLeft, first ? aRight : bRight,
                    time, first ? aScale : bScale);
                range = new float2(math.min(range.x, math.cmin(gap)), math.max(range.y, math.cmax(gap)));
            }
            return range;
        }

        // Distance of the modelled pavement edge beyond the skirt edge on the
        // left and right side of the ribbon at one cross-section.
        private static float2 SkirtGap(MedianRibbon ribbon, MedianSection section,
            NativeCurbFit left, NativeCurbFit right, float time, float scale)
        {
            float2 normal = section.Across.xz / ribbon.HalfWidth;
            return new float2(
                math.dot((left.Marker(time, scale) - (section.Center + section.Across * scale)).xz, normal),
                -math.dot((right.Marker(time, scale) - (section.Center - section.Across * scale)).xz, normal));
        }

        // Cubic Bernstein controls for the same quadratic blend.
        internal static float4 WeightControls(float fraction, float seamFraction) =>
            seamFraction + (fraction - seamFraction) *
            new float4(1f, (2f + SyncEase) / 3f, (1f + SyncEase) / 3f, 0f);

        internal float3 Tangent(float time)
        {
            float3 tangent = math.lerp(MathUtils.Tangent(Inner, time), MathUtils.Tangent(outer, time), Weight(time));
            float weightRate = (fraction - SeamFraction) * (SyncEase - 1f - 2f * SyncEase * time);
            return tangent + weightRate * (MathUtils.Position(outer, time) - MathUtils.Position(Inner, time));
        }

        internal bool Joins(NativeCurbFit other, float3 start, float3 end, float2 startDirection, float2 endDirection)
        {
            float tolerance = MedianRibbon.JoinTolerance;
            return math.distancesq(Curb(0f), start) <= tolerance * tolerance &&
                math.distancesq(other.Curb(0f), end) <= tolerance * tolerance &&
                math.distancesq(Curb(1f), other.Curb(1f)) <= 0.0001f &&
                math.dot(math.normalizesafe(Tangent(0f).xz), startDirection) >= 0.999f &&
                math.dot(math.normalizesafe(-other.Tangent(0f).xz), endDirection) >= 0.999f &&
                math.lengthsq(Tangent(1f) + other.Tangent(1f)) <= 0.0001f;
        }

        internal static void Fit(MedianRibbon ribbon, RailPair first, RailPair second,
            out NativeCurbFit firstLeft, out NativeCurbFit firstRight,
            out NativeCurbFit secondLeft, out NativeCurbFit secondRight)
        {
            var near = first.Measure(ribbon);
            var far = second.Measure(ribbon);
            // Eliminate the four mouth handles from the fit. The two remaining
            // variables are the seam derivatives shared by both node halves.
            near.Reduce(out var nearMatrix, out var nearResidual);
            far.Reduce(out var farMatrix, out var farResidual);
            float2 seam = math.mul(math.inverse(new float2x2(nearMatrix.c0 + farMatrix.c0,
                nearMatrix.c1 + farMatrix.c1)), nearResidual + farResidual);
            if (!math.all(math.isfinite(seam))) seam = 0f;
            float2 seamLength = new float2(math.length(ribbon.CurbTangent(0.5f, true)),
                math.length(ribbon.CurbTangent(0.5f, false))) / 6f;
            seam = math.clamp(seam, -seamLength * 0.5f, seamLength * 0.5f);
            first.Finish(near.Solve(seam), seam, out firstLeft, out firstRight);
            second.Finish(far.Solve(seam), seam, out secondLeft, out secondRight);
        }

        internal readonly struct RailPair
        {
            private readonly Rail left;
            private readonly Rail right;
            private readonly bool far;

            internal RailPair(MedianRibbon ribbon, Bezier4x3 leftInner, Bezier4x3 leftOuter, float leftFraction,
                Bezier4x3 rightInner, Bezier4x3 rightOuter, float rightFraction, bool far)
            {
                this.far = far;
                float start = far ? 1f : 0f;
                float orientation = far ? -1f : 1f;
                left = new Rail(ribbon, leftInner, leftOuter, leftFraction, start, orientation, true);
                right = new Rail(ribbon, rightInner, rightOuter, rightFraction, start, orientation, false);
            }

            internal FitSystem Measure(MedianRibbon ribbon)
            {
                FitSystem fit = default;
                for (int i = 1; i < 16; i++)
                {
                    float t = i / 16f;
                    var section = ribbon.Section(far ? 1f - t * 0.5f : t * 0.5f);
                    float3 leftError = section.Left - left.Baseline.Curb(t);
                    float3 rightError = section.Right - right.Baseline.Curb(t);
                    left.Basis(t, out var leftMouth, out var leftSeam);
                    right.Basis(t, out var rightMouth, out var rightSeam);
                    fit.Add(new float2(leftMouth.x, 0f), new float2(leftSeam.x, 0f), leftError.x);
                    fit.Add(new float2(leftMouth.y, 0f), new float2(leftSeam.y, 0f), leftError.y);
                    fit.Add(new float2(leftMouth.z, 0f), new float2(leftSeam.z, 0f), leftError.z);
                    fit.Add(new float2(0f, rightMouth.x), new float2(0f, rightSeam.x), rightError.x);
                    fit.Add(new float2(0f, rightMouth.y), new float2(0f, rightSeam.y), rightError.y);
                    fit.Add(new float2(0f, rightMouth.z), new float2(0f, rightSeam.z), rightError.z);

                    float2 normal = section.Across.xz / ribbon.HalfWidth;
                    const float widthWeight = 5f;
                    fit.Add(widthWeight * new float2(math.dot(leftMouth.xz, normal), -math.dot(rightMouth.xz, normal)),
                        widthWeight * new float2(math.dot(leftSeam.xz, normal), -math.dot(rightSeam.xz, normal)),
                        widthWeight * math.dot((leftError - rightError).xz, normal));
                }
                return fit;
            }

            internal void Finish(float2 mouth, float2 seam, out NativeCurbFit a, out NativeCurbFit b)
            {
                a = left.Finish(mouth.x, seam.x);
                b = right.Finish(mouth.y, seam.y);
            }
        }

        internal struct FitSystem
        {
            private float2x2 mouthMatrix;
            private float2x2 mixedMatrix;
            private float2x2 seamMatrix;
            private float2 mouthResidual;
            private float2 seamResidual;

            internal void Add(float2 mouth, float2 seam, float error)
            {
                mouthMatrix.c0 += mouth * mouth.x;
                mouthMatrix.c1 += mouth * mouth.y;
                mixedMatrix.c0 += mouth * seam.x;
                mixedMatrix.c1 += mouth * seam.y;
                seamMatrix.c0 += seam * seam.x;
                seamMatrix.c1 += seam * seam.y;
                mouthResidual += mouth * error;
                seamResidual += seam * error;
            }

            internal void Reduce(out float2x2 matrix, out float2 residual)
            {
                var eliminated = math.mul(math.transpose(mixedMatrix), math.inverse(mouthMatrix));
                var projected = math.mul(eliminated, mixedMatrix);
                matrix = new float2x2(seamMatrix.c0 - projected.c0, seamMatrix.c1 - projected.c1);
                residual = seamResidual - math.mul(eliminated, mouthResidual);
            }

            internal float2 Solve(float2 seam) => math.mul(math.inverse(mouthMatrix),
                mouthResidual - math.mul(mixedMatrix, seam));
        }

        private readonly struct Rail
        {
            internal readonly NativeCurbFit Baseline;
            private readonly float3 startDirection;
            private readonly float3 seamDirection;
            private readonly float length;

            internal Rail(MedianRibbon ribbon, Bezier4x3 inner, Bezier4x3 outer, float fraction,
                float startTime, float orientation, bool left)
            {
                float3 start = math.lerp(inner.a, outer.a, fraction);
                var section = ribbon.Section(0.5f);
                float3 join = left ? section.Left : section.Right;
                float3 chord = join - start;
                length = math.length(chord);
                float3 along = math.normalizesafe(chord);
                startDirection = math.normalizesafe(ribbon.CurbTangent(startTime, left) * orientation, along);
                float3 seamTangent = ribbon.CurbTangent(0.5f, left) * (0.5f * orientation);
                seamDirection = math.normalizesafe(seamTangent, along);

                // The seam marker is at the visible curb. Leave a full half-median
                // of lateral space behind it so the native curb tread keeps its width.
                float2 outward = section.Across.xz * ((left ? 1f : -1f) / ribbon.HalfWidth);
                float span = math.dot((outer.d - join).xz, outward);
                float seamFraction = ribbon.HalfWidth / (ribbon.HalfWidth + math.max(span, 0.01f));
                float3 innerJoin = (join - seamFraction * outer.d) / (1f - seamFraction);
                float fractionChange = fraction - seamFraction;
                float3 first = (start - fraction * outer.b +
                    (1f - SyncEase) / 3f * fractionChange * (outer.a - inner.a)) / (1f - fraction);
                float3 second = innerJoin - (seamTangent - 3f * seamFraction * (outer.d - outer.c) +
                    (1f + SyncEase) * fractionChange * (outer.d - innerJoin)) / (3f * (1f - seamFraction));
                Baseline = new NativeCurbFit(new Bezier4x3(inner.a, first, second, innerJoin), outer, fraction, seamFraction);
            }

            internal void Basis(float t, out float3 mouth, out float3 seam)
            {
                float u = 1f - t;
                float fraction = Baseline.fraction;
                float weight = 3f * u * t * (1f - Baseline.Weight(t));
                mouth = startDirection * (weight * u / (1f - fraction));
                seam = -seamDirection * (weight * t / (1f - Baseline.SeamFraction));
            }

            internal NativeCurbFit Finish(float handle, float seam)
            {
                if (!math.isfinite(handle)) handle = length / 3f;
                handle = math.clamp(handle, length * 0.08f, length * 0.8f);
                var curve = Baseline.Inner;
                curve.b += startDirection * (handle / (1f - Baseline.fraction));
                curve.c -= seamDirection * (seam / (1f - Baseline.SeamFraction));
                return new NativeCurbFit(curve, Baseline.outer, Baseline.fraction, Baseline.SeamFraction);
            }
        }
    }
}
