using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    // Where the game places junction pavement that it deforms between two rails,
    // and how a rigid strip is seated on that pavement. The placement follows
    // the game's own copy of it, which the game uses to put objects on a
    // junction's surface.
    internal static class NodeSurface
    {
        // One point of pavement under a strip.
        internal struct Sample
        {
            // Position across the strip, from 0 at its inner edge to 1 at its outer edge.
            public float Across;
            // Time along the ribbon.
            public float Time;
            // Height above the ribbon's cross-section, in meters. It picks the
            // layer of pavement that is in sight where several overlap.
            public float Height;
            // Height above the cross-section as the strip's mesh draws it:
            // straight between the strip's vertices.
            public float Rise;
            // Change of both heights per meter by which the two inner control
            // points of the side street's middle rail are raised.
            public float2 Slope;
            // The corners of the strip's mesh triangle over the point, as
            // lattice vertices, and their weights at the point.
            public int3 Corners;
            public float3 Weights;

            public float HeightAt(float2 settle) => Height + math.dot(Slope, settle);

            public float RiseAt(float2 settle) => Rise + math.dot(Slope, settle);
        }

        internal struct Seating
        {
            // Heights of the strip's inner and outer edge above the ribbon's
            // cross-section, as the control values of a cubic along the ribbon.
            public float4 Inner;
            public float4 Outer;
            // Raise of the two inner control points of the side street's middle rail.
            public float2 Settle;
            // Largest distance between the strip and the pavement under it:
            // anywhere, along the strip's outer edge and along its inner edge.
            public float Floating;
            public float Edge;
            public float Inside;
        }

        // The vertices of the strip's top layer as the game places them. The
        // rows are the rail times of the vertex rows, the columns the positions
        // across the strip of the vertex columns. The game draws the strip flat
        // between them, two triangles to a cell.
        internal sealed class Lattice
        {
            internal readonly List<float> Rows = new List<float>();
            internal readonly List<float> Columns = new List<float>();
            // One value per cell, row by row: 1 when the cell's triangles meet
            // along its diagonal from lower time and lower across to higher time
            // and higher across, -1 along the other diagonal, 0 when the mesh
            // does not say. Both triangulations are then kept clear.
            internal readonly List<sbyte> Diagonals = new List<sbyte>();
            private readonly List<float> bases = new List<float>();
            private readonly List<float4> weights = new List<float4>();

            internal int Count => Rows.Count * Columns.Count;

            internal float LargestStep
            {
                get
                {
                    float largest = 0f;
                    for (int i = 1; i < Rows.Count; i++) largest = math.max(largest, Rows[i] - Rows[i - 1]);
                    return largest;
                }
            }

            internal void Clear()
            {
                Rows.Clear();
                Columns.Clear();
                Diagonals.Clear();
                bases.Clear();
                weights.Clear();
            }

            // A lattice of even rows and columns whose triangulation is not known.
            internal static Lattice Even(int rows, int columns)
            {
                var lattice = new Lattice();
                for (int i = 0; i <= rows; i++) lattice.Rows.Add(i / (float)rows);
                for (int i = 0; i <= columns; i++) lattice.Columns.Add(i / (float)columns);
                for (int i = 0; i < rows * columns; i++) lattice.Diagonals.Add(0);
                return lattice;
            }

            // The lattice of the lane pieces that a junction mesh carries on its
            // shape rails, after the game's placement of a half-length piece that
            // keeps its shape: a vertex at x and z of a piece centered at c goes to
            // (c - x) / width + 1/2 across the mesh and to (halfLength - z) / width
            // along it, clamped at 1. The mesh's middle "band" meters are spread
            // over the strip's width, and the rails cover the first
            // min(halfLength, width) / width of the mesh's length. Each piece
            // gives its vertices and its triangles as vertex indices.
            internal bool Build(float band, float width, float halfLength, IReadOnlyList<float> centers,
                IReadOnlyList<float3[]> vertices, IReadOnlyList<int[]> triangles)
            {
                Clear();
                float span = math.min(halfLength, width);
                if (band <= 0f || width <= 0f || span <= 0f || centers.Count == 0) return false;
                for (int lane = 0; lane < centers.Count; lane++)
                {
                    float top = Top(vertices[lane]);
                    foreach (var p in vertices[lane])
                    {
                        if (!OnTop(p, top)) continue;
                        Add(Rows, Row(p.z, halfLength, width, span));
                        Add(Columns, Column(p.x, centers[lane], band));
                    }
                }
                Rows.Sort();
                Columns.Sort();
                if (Rows.Count < 2 || Columns.Count < 2 || Rows[0] > 0.001f || Rows[Rows.Count - 1] < 0.999f) return false;
                int cells = (Rows.Count - 1) * (Columns.Count - 1);
                var conflicted = new bool[cells];
                for (int i = 0; i < cells; i++) Diagonals.Add(0);
                for (int lane = 0; lane < centers.Count; lane++)
                {
                    var points = vertices[lane];
                    var indices = triangles[lane];
                    float top = Top(points);
                    for (int t = 0; t + 2 < indices.Length; t += 3)
                    {
                        int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                        if (a < 0 || b < 0 || c < 0 || a >= points.Length || b >= points.Length || c >= points.Length ||
                            !OnTop(points[a], top) || !OnTop(points[b], top) || !OnTop(points[c], top)) continue;
                        Diagonal(points[a], points[b], centers[lane], band, halfLength, width, span, conflicted);
                        Diagonal(points[b], points[c], centers[lane], band, halfLength, width, span, conflicted);
                        Diagonal(points[c], points[a], centers[lane], band, halfLength, width, span, conflicted);
                    }
                }
                return true;
            }

            // The same lattice for rails that run the other way along the ribbon.
            internal void Turn()
            {
                int columns = Columns.Count - 1;
                int rows = Rows.Count - 1;
                var turned = new List<sbyte>(Diagonals.Count);
                for (int row = rows - 1; row >= 0; row--)
                    for (int column = 0; column < columns; column++) turned.Add((sbyte)-Diagonals[row * columns + column]);
                Diagonals.Clear();
                Diagonals.AddRange(turned);
                for (int i = 0; i < Rows.Count; i++) Rows[i] = 1f - Rows[i];
                Rows.Reverse();
                bases.Clear();
                weights.Clear();
            }

            // Fixes the heights of the strip's mesh before it is lifted: its rails
            // follow the ribbon's cross-section at the strip's two edges.
            internal void Prepare(MedianRibbon ribbon, float side, float inner, float outer)
            {
                bases.Clear();
                weights.Clear();
                foreach (float row in Rows)
                {
                    weights.Add(Bernstein(row));
                    foreach (float column in Columns)
                        bases.Add(ribbon.Offset(row, side * math.lerp(inner, outer, column)).y);
                }
            }

            internal sbyte Diagonal(int column, int row) => Diagonals[row * (Columns.Count - 1) + column];

            internal float Base(int vertex) => bases[vertex];

            internal float ColumnOf(int vertex) => Columns[vertex % Columns.Count];

            internal float4 WeightsOf(int vertex) => weights[vertex / Columns.Count];

            internal float VertexLift(int vertex, float4 inner, float4 outer)
            {
                float4 weight = WeightsOf(vertex);
                return math.lerp(math.dot(weight, inner), math.dot(weight, outer), ColumnOf(vertex));
            }

            internal void Locate(float across, float time, out int column, out int row, out float u, out float v)
            {
                Find(Columns, across, out column, out u);
                Find(Rows, time, out row, out v);
            }

            // The triangle of a cell at (u, v) inside it, for one of its diagonals.
            internal void Triangle(int column, int row, float u, float v, int diagonal, out int3 corners,
                out float3 weights)
            {
                int columns = Columns.Count;
                int lower = row * columns + column;
                int upper = lower + columns;
                if (diagonal >= 0)
                {
                    // Split along lower left to upper right.
                    if (u >= v)
                    {
                        corners = new int3(lower, lower + 1, upper + 1);
                        weights = new float3(1f - u, u - v, v);
                    }
                    else
                    {
                        corners = new int3(lower, upper, upper + 1);
                        weights = new float3(1f - v, v - u, u);
                    }
                }
                else if (u + v <= 1f)
                {
                    corners = new int3(lower, lower + 1, upper);
                    weights = new float3(1f - u - v, u, v);
                }
                else
                {
                    corners = new int3(upper + 1, lower + 1, upper);
                    weights = new float3(u + v - 1f, 1f - v, 1f - u);
                }
            }

            internal float Lift(float4 inner, float4 outer, int3 corners, float3 weights) =>
                weights.x * VertexLift(corners.x, inner, outer) + weights.y * VertexLift(corners.y, inner, outer) +
                weights.z * VertexLift(corners.z, inner, outer);

            // Height of the drawn strip above the ribbon's cross-section as the
            // strip's mesh draws that cross-section, at a point of the strip.
            // With "diagonal" 0 the cell's own diagonal is used, or the lower of
            // the two when it is not known.
            internal float Lift(float4 inner, float4 outer, float across, float time, int diagonal = 0)
            {
                Locate(across, time, out int column, out int row, out float u, out float v);
                int known = Diagonal(column, row);
                if (diagonal == 0 && known != 0) diagonal = known;
                if (diagonal != 0)
                {
                    Triangle(column, row, u, v, diagonal, out var corners, out var weights);
                    return Lift(inner, outer, corners, weights);
                }
                Triangle(column, row, u, v, 1, out var first, out var firstWeights);
                Triangle(column, row, u, v, -1, out var second, out var secondWeights);
                return math.min(Lift(inner, outer, first, firstWeights), Lift(inner, outer, second, secondWeights));
            }

            // Height of the strip's mesh before it is lifted, as drawn.
            internal float Drawn(int3 corners, float3 weights) =>
                weights.x * bases[corners.x] + weights.y * bases[corners.y] + weights.z * bases[corners.z];

            private void Diagonal(float3 a, float3 b, float center, float band, float halfLength, float width, float span,
                bool[] conflicted)
            {
                if (math.abs(a.x - b.x) < 0.001f || math.abs(a.z - b.z) < 0.001f) return;
                float acrossA = Column(a.x, center, band), acrossB = Column(b.x, center, band);
                float timeA = Row(a.z, halfLength, width, span), timeB = Row(b.z, halfLength, width, span);
                if (math.abs(timeA - timeB) < 0.00001f) return;
                int column = IndexOf(Columns, math.min(acrossA, acrossB));
                int row = IndexOf(Rows, math.min(timeA, timeB));
                if (column < 0 || row < 0 || IndexOf(Columns, math.max(acrossA, acrossB)) != column + 1 ||
                    IndexOf(Rows, math.max(timeA, timeB)) != row + 1) return;
                int cell = row * (Columns.Count - 1) + column;
                sbyte sign = (sbyte)((acrossB - acrossA) * (timeB - timeA) > 0f ? 1 : -1);
                if (conflicted[cell]) return;
                if (Diagonals[cell] == 0) Diagonals[cell] = sign;
                else if (Diagonals[cell] != sign)
                {
                    Diagonals[cell] = 0;
                    conflicted[cell] = true;
                }
            }

            private static float Row(float z, float halfLength, float width, float span) =>
                math.saturate((halfLength - z) / width) * width / span;

            private static float Column(float x, float center, float band) => (center - x) / band + 0.5f;

            // The game keeps only the half of a half-length piece at z >= 0.
            private static bool OnTop(float3 p, float top) => p.z >= -0.01f && p.y >= top - 0.01f;

            private static float Top(float3[] points)
            {
                float top = float.NegativeInfinity;
                foreach (var p in points) if (p.z >= -0.01f) top = math.max(top, p.y);
                return top;
            }

            private static void Add(List<float> values, float value)
            {
                if (IndexOf(values, value) < 0) values.Add(value);
            }

            private static int IndexOf(List<float> values, float value)
            {
                for (int i = 0; i < values.Count; i++) if (math.abs(values[i] - value) < 0.0001f) return i;
                return -1;
            }

            private static void Find(List<float> knots, float value, out int index, out float fraction)
            {
                index = 0;
                while (index < knots.Count - 2 && value > knots[index + 1]) index++;
                float length = knots[index + 1] - knots[index];
                fraction = length > 0f ? math.saturate((value - knots[index]) / length) : 0f;
            }
        }

        internal static float Remap(float t, float4 offsets, float4 targets)
        {
            if (t < offsets.x) return targets.x * math.saturate(t / offsets.x);
            if (t < offsets.y)
                return math.lerp(targets.x, targets.y, math.saturate((t - offsets.x) / (offsets.y - offsets.x)));
            if (t < offsets.z)
                return math.lerp(targets.y, targets.z, math.saturate((t - offsets.y) / (offsets.z - offsets.y)));
            if (t < offsets.w)
                return math.lerp(targets.z, targets.w, math.saturate((t - offsets.z) / (offsets.w - offsets.z)));
            return math.lerp(targets.w, 1f, math.saturate((t - offsets.w) / (1f - offsets.w)));
        }

        // The line along a junction half at a share of the way from its left
        // rail to its right rail. "middle" is the share at which the line's
        // third control point lies between the rails' third control points.
        internal static Bezier4x3 Curve(Bezier4x3 left, Bezier4x3 right, float share, float4 offsets, float4 targets,
            out float middle)
        {
            float end = Remap(share, offsets, targets);
            float startWidth = math.distance(left.a.xz, right.a.xz);
            float endWidth = math.distance(left.d.xz, right.d.xz);
            float4 blended = math.lerp(offsets * startWidth, targets * endWidth, 0.5f) /
                math.max(0.000001f, math.lerp(startWidth, endWidth, 0.5f));
            middle = Remap(share, offsets, blended);
            return new Bezier4x3(math.lerp(left.a, right.a, share), math.lerp(left.b, right.b, share),
                math.lerp(left.c, right.c, middle), math.lerp(left.d, right.d, end));
        }

        // Records the pavement of a deformed junction half inside a band beside
        // the median. "settles" names the rail whose inner control points may
        // be raised or lowered: -1 for the left rail, 1 for the right rail and
        // 0 for neither. The lattice must be prepared for the band.
        internal static void Rise(MedianRibbon ribbon, float side, float inner, float outer, Lattice lattice,
            Bezier4x3 left, Bezier4x3 right, float4 offsets, float4 targets, float from, float to, int settles,
            List<Sample> samples, int across = 24, int along = 48)
        {
            for (int i = 0; i <= across; i++)
            {
                float share = math.lerp(from, to, i / (float)across);
                var curve = Curve(left, right, share, offsets, targets, out float middle);
                float2 weight = settles > 0 ? new float2(share, middle)
                    : settles < 0 ? new float2(1f - share, 1f - middle) : float2.zero;
                for (int j = 0; j <= along; j++)
                {
                    float step = j / (float)along;
                    float3 point = MathUtils.Position(curve, step);
                    if (!ribbon.Locate(point, out float time, out float lateral)) continue;
                    float distance = lateral * side;
                    if (distance < inner - 0.05f || distance > outer + 0.05f) continue;
                    float rest = 1f - step;
                    float position = math.saturate((distance - inner) / (outer - inner));
                    var sample = new Sample
                    {
                        Across = position,
                        Time = time,
                        Height = point.y - ribbon.Offset(time, lateral).y,
                        Slope = weight * new float2(3f * rest * rest * step, 3f * rest * step * step)
                    };
                    lattice.Locate(position, time, out int column, out int row, out float u, out float v);
                    int diagonal = lattice.Diagonal(column, row);
                    // Without a known diagonal the point is kept clear of both triangulations.
                    for (int split = diagonal == 0 ? 1 : diagonal; ; split = -1)
                    {
                        lattice.Triangle(column, row, u, v, split, out sample.Corners, out sample.Weights);
                        sample.Rise = point.y - lattice.Drawn(sample.Corners, sample.Weights);
                        samples.Add(sample);
                        if (diagonal != 0 || split < 0) break;
                    }
                }
            }
        }

        // The strip's straight segments differ from the divider's along a curve,
        // so its inner edge reaches over the divider's skirt by up to the
        // sagitta of its longest segment. The reach is capped at a share of the
        // skirt, and the steps that end the inner edge share the rest.
        internal const float SkirtReachShare = 0.3f;

        internal static float SkirtReach(MedianRibbon ribbon, Lattice lattice, float skirt) =>
            math.min(0.02f + ribbon.ChordError(lattice.LargestStep), skirt * SkirtReachShare);

        // The width of each inner step. It depends on the divider alone, so
        // that every junction with the same roads can share one strip mesh.
        internal static float InnerStepTread(float skirt, int steps, float widest) =>
            steps <= 0 ? 0f : math.min(widest, skirt * (1f - SkirtReachShare) / steps);

        // Which half of a through road lies on the strip's side. The first
        // approach runs toward the junction and the second away from it.
        internal static bool ThroughLeftHalf(float side, bool second) => second ? side < 0f : side > 0f;

        // The part of a through road's junction half that lies in the band. Its
        // shares run from the half's left rail to its right rail. The median is
        // the right rail of a left half and the left rail of a right half.
        internal static void Through(MedianRibbon ribbon, float side, float inner, float outer, Lattice lattice,
            Bezier4x3 left, Bezier4x3 right, float4 offsets, float4 targets, float halfWidth, bool leftHalf,
            List<Sample> samples, int across = 24, int along = 48)
        {
            float from = leftHalf ? 1f - outer / halfWidth : inner / halfWidth;
            float to = leftHalf ? 1f - inner / halfWidth : outer / halfWidth;
            Rise(ribbon, side, inner, outer, lattice, left, right, offsets, targets, from, to, 0, samples, across, along);
        }

        // Whether the strip's rails are turned around. The game's placement of
        // meshes that keep their shape draws a mesh face up when the rail of its
        // lower side lies on the left of the direction in which the rails run,
        // so both rails run back along the ribbon when the strip's inner edge
        // would lie on their right. Heights do not change it.
        internal static bool Turns(MedianRibbon ribbon, float side, float inner, float outer, bool reverse)
        {
            var innerRail = ribbon.Rail(side * inner);
            var outerRail = ribbon.Rail(side * outer);
            float2 forward = MathUtils.Tangent(innerRail, 0.5f).xz;
            float2 inward = (MathUtils.Position(innerRail, 0.5f) - MathUtils.Position(outerRail, 0.5f)).xz;
            return (math.dot(MathUtils.Left(forward), inward) < 0f) != reverse;
        }

        // The strip's two edges as rails, with their heights. "drift" is the
        // largest horizontal distance between a vertex of the strip's top layer
        // and the point of the ribbon it was seated at. The lattice runs along
        // the ribbon, so it is turned already when the rails are.
        internal static bool Rails(MedianRibbon ribbon, float side, float inner, float outer, Seating seating,
            Lattice lattice, bool reverse, out Bezier4x3 innerRail, out Bezier4x3 outerRail, out float drift)
        {
            innerRail = ribbon.Rail(side * inner);
            outerRail = ribbon.Rail(side * outer);
            innerRail.a.y += seating.Inner.x;
            innerRail.b.y += seating.Inner.y;
            innerRail.c.y += seating.Inner.z;
            innerRail.d.y += seating.Inner.w;
            outerRail.a.y += seating.Outer.x;
            outerRail.b.y += seating.Outer.y;
            outerRail.c.y += seating.Outer.z;
            outerRail.d.y += seating.Outer.w;
            drift = 0f;
            foreach (float row in lattice.Rows)
            {
                float3 near = MathUtils.Position(innerRail, row);
                float3 far = MathUtils.Position(outerRail, row);
                foreach (float column in lattice.Columns)
                {
                    float3 seated = ribbon.Offset(row, side * math.lerp(inner, outer, column));
                    drift = math.max(drift, math.distance(math.lerp(near, far, column).xz, seated.xz));
                }
            }
            bool turn = Turns(ribbon, side, inner, outer, reverse);
            if (turn)
            {
                innerRail = MathUtils.Invert(innerRail);
                outerRail = MathUtils.Invert(outerRail);
            }
            return turn;
        }

        private static float4 Bernstein(float time)
        {
            float rest = 1f - time;
            return new float4(rest * rest * rest, 3f * rest * rest * time, 3f * rest * time * time, time * time * time);
        }

        // Weight of the strip's two long edges and of its two ends in the
        // seating. A gap under an edge is in plain sight, while a gap under
        // the strip's middle is covered.
        private const float EdgeWeight = 12f;
        private const float InnerWeight = 12f;
        private const float EndWeight = 6f;
        // Weight of a place where the strip would cut into the pavement,
        // against one where it floats above it.
        private const double Penalty = 400.0;
        // Pull of the side street's middle rail toward an even grade.
        private const double GradeWeight = 8.0;
        // Pull of the strip's four corners toward the least height above the
        // road. The ends cannot be stepped down like the long edges.
        private const double EndPin = 2000.0;
        // The unknowns that are the heights of the strip's corners: the first
        // and last control value of its inner and of its outer edge.
        private static readonly int[] Corners = { 0, 3, 4, 7 };

        // Seats a strip on the top layer of the pavement under it. A junction's
        // surface warps by several centimeters between its mouths, so a strip
        // that stayed on the ribbon's cross-section would float well above part
        // of it. The strip's heights are fitted to the pavement as the strip's
        // mesh draws them, flat on each triangle of its lattice, and the whole
        // strip is then raised until it clears every sample by the margin.
        //
        // A side street that arrives on a grade bends over or sags along its
        // middle rail, which leaves a ridge or a trough across the through lanes
        // that no smooth strip can follow. With "settle" the heights of that
        // rail's two inner control points are fitted as well. "grade" is the
        // raise that would put them on an even grade between the rail's ends.
        //
        // False when the strip would leave the cross-section, or the rail its
        // own heights, by more than the limit.
        internal static bool Seat(List<Sample> samples, Lattice lattice, float margin, float limit, bool settle,
            float2 grade, out Seating seating)
        {
            seating = new Seating { Inner = margin, Outer = margin, Floating = margin, Edge = margin, Inside = margin };
            if (samples.Count == 0) return true;

            // Several layers of pavement overlap in a junction. Only the highest
            // one in each cell of a grid over the strip is in sight.
            const int across = 10;
            int along = 8 * math.max(1, lattice.Rows.Count - 1);
            var top = new int[across * along];

            // Least squares in which a place that the strip would cut into counts
            // far more than one it floats above, repeated until the strip rests
            // on the pavement from above.
            int unknowns = settle ? 10 : 8;
            var matrix = new double[unknowns, unknowns + 1];
            var row = new double[unknowns];
            float4 inner = margin, outer = margin;
            float2 raised = float2.zero;
            for (int pass = 0; pass < 32; pass++)
            {
                Top(samples, raised, top, across, along);
                System.Array.Clear(matrix, 0, matrix.Length);
                for (int index = 0; index < samples.Count; index++)
                {
                    var sample = samples[index];
                    bool cuts = pass != 0 && lattice.Lift(inner, outer, sample.Corners, sample.Weights) <
                        sample.RiseAt(raised) + margin;
                    if (!cuts && top[Cell(sample, across, along)] != index) continue;
                    double weight = Visible(sample.Across, sample.Time);
                    if (cuts) weight *= Penalty;
                    System.Array.Clear(row, 0, row.Length);
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int vertex = sample.Corners[corner];
                        float share = sample.Weights[corner];
                        float4 control = lattice.WeightsOf(vertex);
                        float column = lattice.ColumnOf(vertex);
                        for (int k = 0; k < 4; k++)
                        {
                            row[k] += share * (1f - column) * control[k];
                            row[k + 4] += share * column * control[k];
                        }
                    }
                    if (settle)
                    {
                        row[8] = -sample.Slope.x;
                        row[9] = -sample.Slope.y;
                    }
                    for (int i = 0; i < unknowns; i++)
                    {
                        for (int j = 0; j < unknowns; j++) matrix[i, j] += weight * row[i] * row[j];
                        matrix[i, unknowns] += weight * row[i] * (sample.Rise + margin);
                    }
                }
                // A small pull toward zero keeps heights steady where samples are few.
                for (int i = 0; i < 8; i++) matrix[i, i] += 0.0001;
                // The strip's ends lie across the lanes at the two mouths, where
                // the pavement is the road's own cross-section.
                foreach (int corner in Corners)
                {
                    matrix[corner, corner] += EndPin;
                    matrix[corner, unknowns] += EndPin * margin;
                }
                if (settle)
                {
                    matrix[8, 8] += GradeWeight;
                    matrix[9, 9] += GradeWeight;
                    matrix[8, 10] += GradeWeight * grade.x;
                    matrix[9, 10] += GradeWeight * grade.y;
                }
                if (!Solve(matrix, unknowns)) return false;
                for (int k = 0; k < 4; k++)
                {
                    inner[k] = (float)matrix[k, unknowns];
                    outer[k] = (float)matrix[k + 4, unknowns];
                }
                if (settle) raised = new float2((float)matrix[8, unknowns], (float)matrix[9, unknowns]);
            }

            // The lift is an average of control values whose weights sum to one,
            // so adding to every control value raises the whole strip.
            float raise = float.NegativeInfinity;
            foreach (var sample in samples)
                raise = math.max(raise, sample.RiseAt(raised) + margin -
                    lattice.Lift(inner, outer, sample.Corners, sample.Weights));
            inner += raise;
            outer += raise;
            if (!math.all(math.isfinite(inner)) || !math.all(math.isfinite(outer)) || !math.all(math.isfinite(raised)))
                return false;
            Top(samples, raised, top, across, along);
            float floating = 0f, edge = 0f, inside = 0f, height = 0f;
            foreach (int index in top)
            {
                if (index < 0) continue;
                var sample = samples[index];
                float lift = lattice.Lift(inner, outer, sample.Corners, sample.Weights);
                float gap = lift - sample.RiseAt(raised);
                floating = math.max(floating, gap);
                if (sample.Across > 0.9f) edge = math.max(edge, gap);
                if (sample.Across < 0.1f) inside = math.max(inside, gap);
                height = math.max(height, math.abs(lift));
            }
            seating = new Seating
            {
                Inner = inner, Outer = outer, Settle = raised, Floating = floating, Edge = edge, Inside = inside
            };
            return height <= limit && math.cmax(math.abs(raised)) <= limit;
        }

        private static void Top(List<Sample> samples, float2 raised, int[] top, int across, int along)
        {
            for (int i = 0; i < top.Length; i++) top[i] = -1;
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                int cell = Cell(sample, across, along);
                if (top[cell] < 0 || sample.HeightAt(raised) > samples[top[cell]].HeightAt(raised)) top[cell] = i;
            }
        }

        private static int Cell(Sample sample, int across, int along) =>
            math.min(across - 1, (int)(sample.Across * across)) * along + math.min(along - 1, (int)(sample.Time * along));

        // How much a gap under the strip at this place shows.
        private static float Visible(float across, float time)
        {
            float outer = math.saturate((across - 0.75f) / 0.25f);
            float inner = math.saturate((0.25f - across) / 0.25f);
            float end = math.saturate((math.abs(time - 0.5f) - 0.4f) / 0.1f);
            return 1f + EdgeWeight * outer * outer + InnerWeight * inner * inner + EndWeight * end * end;
        }

        // Solves the system in place. Its last column holds the solution afterwards.
        private static bool Solve(double[,] matrix, int unknowns)
        {
            for (int column = 0; column < unknowns; column++)
            {
                int pivot = column;
                for (int i = column + 1; i < unknowns; i++)
                    if (System.Math.Abs(matrix[i, column]) > System.Math.Abs(matrix[pivot, column])) pivot = i;
                if (System.Math.Abs(matrix[pivot, column]) < 1e-12) return false;
                for (int j = column; j <= unknowns; j++)
                    (matrix[column, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[column, j]);
                for (int i = 0; i < unknowns; i++)
                {
                    if (i == column) continue;
                    double factor = matrix[i, column] / matrix[column, column];
                    for (int j = column; j <= unknowns; j++) matrix[i, j] -= factor * matrix[column, j];
                }
            }
            for (int i = 0; i < unknowns; i++) matrix[i, unknowns] /= matrix[i, i];
            return true;
        }
    }
}
