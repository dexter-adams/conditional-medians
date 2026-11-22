using System;
using System.Globalization;
using System.IO;
using Colossal.Mathematics;
using Game.Net;
using Unity.Mathematics;

namespace ConditionalMedians2
{
    // Geometry records for investigating render defects. They are written to the
    // mod log while a file named diagnostics.txt sits in the game's Mods\ConditionalMedians2 folder (see Path).
    // Lines of the form name=value in that file adjust the through-lane strip
    // while the game runs:
    //   strip=off       draws no strip
    //   clearance=0.02  least height of the strip above the pavement, in meters
    //   lift=0.05       fixed height in place of the fitted one
    //   reverse=on      runs the strip's rails the other way
    //   settle=off      leaves the side street's middle rail at its own heights
    //   native=1234     draws the divider of the closed junction with that
    //                   entity index with the game's native deformation
    //   streets=native  gives side streets the game's own junction meshes back
    internal static class MedianDiagnostics
    {
        private static readonly string Path = System.IO.Path.Combine(
            UnityEngine.Application.persistentDataPath, "Mods", "ConditionalMedians2", "diagnostics.txt");
        private static readonly CultureInfo Plain = CultureInfo.InvariantCulture;
        private static DateTime stamp;
        private static float nextPoll;

        internal static bool Enabled { get; private set; }
        internal static bool Strip { get; private set; } = true;
        internal static bool StripReverse { get; private set; }
        internal static bool StripSettle { get; private set; } = true;
        internal static float StripClearance { get; private set; } = DefaultClearance;
        internal static float StripLift { get; private set; } = float.NaN;
        internal static int NativeJunction { get; private set; } = -1;
        internal static bool StreetsNative { get; private set; }

        internal const float DefaultClearance = 0.012f;

        static MedianDiagnostics() { Read(); }

        // True when the file changed since the last call. Closed junctions are fitted again then.
        internal static bool Poll()
        {
            if (UnityEngine.Time.unscaledTime < nextPoll) return false;
            nextPoll = UnityEngine.Time.unscaledTime + 1f;
            return Read();
        }

        private static bool Read()
        {
            bool exists;
            DateTime written = default;
            try
            {
                exists = File.Exists(Path);
                if (exists) written = File.GetLastWriteTimeUtc(Path);
            }
            catch (Exception)
            {
                return false;
            }
            if (exists == Enabled && written == stamp) return false;
            bool strip = true, reverse = false, settle = true, streets = false;
            float clearance = DefaultClearance, lift = float.NaN;
            int native = -1;
            if (exists)
            {
                try
                {
                    foreach (string line in File.ReadAllLines(Path))
                    {
                        int split = line.IndexOf('=');
                        if (split <= 0) continue;
                        string name = line.Substring(0, split).Trim().ToLowerInvariant();
                        string value = line.Substring(split + 1).Trim().ToLowerInvariant();
                        bool on = value == "on" || value == "1" || value == "true";
                        bool number = float.TryParse(value, NumberStyles.Float, Plain, out float amount);
                        if (name == "strip") strip = on;
                        else if (name == "reverse") reverse = on;
                        else if (name == "settle") settle = on;
                        else if (name == "streets") streets = value == "native";
                        else if (name == "clearance" && number) clearance = math.clamp(amount, 0f, 0.2f);
                        else if (name == "lift" && number) lift = math.clamp(amount, -0.2f, 0.5f);
                        else if (name == "native" && int.TryParse(value, NumberStyles.Integer, Plain, out int index))
                            native = index;
                    }
                }
                catch (Exception)
                {
                    // A file that is held open or being written keeps the last
                    // settings and is read again at the next poll.
                    return false;
                }
            }
            Enabled = exists;
            stamp = written;
            Strip = strip;
            StripReverse = reverse;
            StripSettle = settle;
            StripClearance = clearance;
            StripLift = lift;
            NativeJunction = native;
            StreetsNative = streets;
            return true;
        }

        internal static string N(float value) => value.ToString("F5", Plain);

        internal static string V(float2 v) => $"({N(v.x)},{N(v.y)})";

        internal static string V(float3 v) => $"({N(v.x)},{N(v.y)},{N(v.z)})";

        internal static string V(float4 v) => $"({N(v.x)},{N(v.y)},{N(v.z)},{N(v.w)})";

        internal static string B(Bezier4x3 c) => $"[{V(c.a)} {V(c.b)} {V(c.c)} {V(c.d)}]";

        internal static string M(float4x4 m) => $"[{V(m.c0)} {V(m.c1)} {V(m.c2)} {V(m.c3)}]";

        internal static string G(EdgeNodeGeometry g) =>
            $"LL{B(g.m_Left.m_Left)} LR{B(g.m_Left.m_Right)} RL{B(g.m_Right.m_Left)} RR{B(g.m_Right.m_Right)} " +
            $"M{B(g.m_Middle)} TL{V(g.m_SyncVertexTargetsLeft)} TR{V(g.m_SyncVertexTargetsRight)} " +
            $"lenL{V(g.m_Left.m_Length)} lenR{V(g.m_Right.m_Length)} origin{V(MathUtils.Center(g.m_Bounds))}";
    }
}
