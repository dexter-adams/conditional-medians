using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using ConditionalMedians2;
using Unity.Mathematics;

var straight = Curve(0, 0, 0, 8, 0, 16, 0, 24);
var gentle = Curve(0, 0, 0, 7, 2, 14, 5, 21);
var pronounced = Curve(0, 0, 0, 8, 6, 17, 13, 18);
var asymmetric = Curve(0, 0, 0.25f, 6.4f, -7.55f, 11.3f, -7.325f, 17.72f);

// Center curves of the two closed test junctions in a test city, as the
// game logged them: Beechwood Street turns about 10 degrees across its junction,
// Alyssa Street is straight.
var beechwood = C(-185.20540f, 469.32320f, -886.16550f, -190.25780f, 469.27810f, -885.49740f,
    -195.36150f, 469.14610f, -885.30670f, -200.45070f, 468.84380f, -885.57880f);
var alyssaStreet = C(-198.64920f, 465.32100f, -936.46410f, -192.88950f, 465.49790f, -936.71480f,
    -187.12970f, 465.62740f, -936.96580f, -181.37000f, 465.72570f, -937.21660f);

const float tile = 1.25f;
MedianTexturePhase.Map(pronounced, 39.7f, 17.2f, tile, true, true, out var uvNear, out var uvFar);
Check(uvNear.x == 39.7f && uvNear.w == uvFar.w, "texture station continues into both halves");
Check(math.abs((uvFar.x - 17.2f) / tile - math.round((uvFar.x - 17.2f) / tile)) < 0.0001f,
    "texture reaches the second mouth at the native repeating phase");
Check(math.abs((uvNear.w - uvNear.z) + (uvFar.w - uvFar.z)) < 0.0001f,
    "texture derivative is continuous at the center seam");
MedianTexturePhase.Map(straight, 0f, 0f, tile, false, true, out uvNear, out uvFar);
Check(uvNear.x > uvNear.w && uvFar.x < uvFar.w, "texture proceeds in reverse on a reversed approach");

// One texture repeat of the stock divided road is 32 m and spans about thirty
// curb stones. Beechwood Street's junction would need 12.7 m of slack over
// 15.3 m to reach the next repeat, which drew the stones at 0.55 of their length.
const float repeat = 32f;
MedianTexturePhase.Map(beechwood, 0f, 36.03448f, repeat, false, true, out uvNear, out uvFar);
MathUtils.Divide(beechwood, out var beechwoodNear, out var beechwoodFar, 0.5f);
float beechwoodLength = MathUtils.Length(beechwoodNear) + MathUtils.Length(beechwoodFar);
Check(math.abs(uvFar.x + 15.2875f) < 0.002f && math.abs(uvNear.w + 7.6405f) < 0.002f && uvNear.w == uvFar.w,
    "Beechwood Street keeps the native texture scale and restarts at its second mouth");
Check(math.abs(uvNear.x - uvFar.x - beechwoodLength) < 0.001f &&
    math.abs(math.abs(uvNear.w - uvNear.x) - MathUtils.Length(beechwoodNear)) < 0.001f &&
    math.abs(math.abs(uvFar.w - uvFar.x) - MathUtils.Length(beechwoodFar)) < 0.001f,
    "the native scale holds in each half");
Check(math.abs(math.abs(uvNear.y - uvNear.x) - math.distance(beechwoodNear.a, beechwoodNear.b)) < 0.001f &&
    math.abs(math.abs(uvFar.w - uvFar.z) - math.distance(beechwoodFar.a, beechwoodFar.b)) < 0.001f,
    "the inner texture controls follow the curve's own controls at the native scale");
// Alyssa Street's junction ends 0.83 m from a repeat over 17.3 m.
MedianTexturePhase.Map(alyssaStreet, 45.86823f, 0f, repeat, true, true, out uvNear, out uvFar);
Check(math.abs(uvFar.x - 64f) < 0.0001f && math.abs(uvNear.w - 54.9348f) < 0.002f,
    "Alyssa Street reaches the next repeat with a small stretch");

// The repeat is matched only within the stretch limit, and only when the second
// road's distance runs the same way as the first road's.
var run = Curve(0, 0, 0, 6.6667f, 0, 13.3333f, 0, 20);
float limit = MedianTexturePhase.MaximumStretch * 20f;
MedianTexturePhase.Map(run, 0f, 20f + limit - 0.05f, repeat, true, true, out uvNear, out uvFar);
Check(math.abs(uvFar.x - (20f + limit - 0.05f)) < 0.002f, "a repeat just inside the stretch limit is matched");
MedianTexturePhase.Map(run, 0f, 20f + limit + 0.05f, repeat, true, true, out uvNear, out uvFar);
Check(math.abs(uvFar.x - 20f) < 0.002f, "a repeat just outside the stretch limit keeps the native scale");
MedianTexturePhase.Map(run, 0f, 20f - limit - 0.05f, repeat, true, true, out uvNear, out uvFar);
Check(math.abs(uvFar.x - 20f) < 0.002f, "the limit applies to a shorter repeat as well");
foreach (bool forward in new[] { true, false })
{
    float direction = forward ? 1f : -1f;
    MedianTexturePhase.Map(run, 0f, direction * 20f + 0.3f, repeat, forward, false, out uvNear, out uvFar);
    Check(math.abs(uvFar.x - direction * 20f) < 0.002f && uvNear.w == uvFar.w,
        "mirrored roads keep the native scale even beside a repeat");
    MedianTexturePhase.Map(run, 0f, direction * 20f + 0.3f, repeat, forward, true, out uvNear, out uvFar);
    Check(math.abs(uvFar.x - (direction * 20f + 0.3f)) < 0.002f && uvNear.w == uvFar.w,
        "roads that run the same way match a repeat in either direction");
}
var weights = NativeCurbFit.WeightControls(0.12f, 0.08f);
var outerStation = new float4(0f, 5f, 7f, 10f);
var innerStation = MedianTexturePhase.Compensate(uvNear, outerStation, weights);
Check(math.abs(BlendStation(1f) - uvNear.w) < 0.0001f, "curb texture phase at the seam");
Check(math.abs((BlendStation(1f) - BlendStation(0.999f)) / 0.003f - (uvNear.w - uvNear.z)) < 0.003f,
    "curb texture scale at the seam");
float BlendStation(float t) => math.lerp(Evaluate(innerStation, t), Evaluate(outerStation, t), Evaluate(weights, t));

CheckRibbon("straight", straight, 0.75f, 8f, 0f);
CheckRibbon("gentle curve", gentle, 1.5f, 10f, 2f);
CheckRibbon("pronounced curve", pronounced, 2.1f, 12f, 4f);
CheckRibbon("asymmetric Road Builder approaches", asymmetric, 2.1f, 12f, 3f);
CheckRibbon("wide divided road", gentle, 3.25f, 15f, 4f);

// The two closed junctions of a test city, from the rails the game
// computed for them: left outer, left inner, right inner and right outer rail
// of each approach. The curved one showed a hairline along the skirt before the
// pavement was tucked under it.
CheckLogged("Beechwood Street junction", beechwood,
    new MedianMouth(new float3(-185.28290f, 469.32200f, -886.76050f), new float3(-185.12780f, 469.32450f, -885.57050f), new float3(-4.54988f, -0.04066f, 0.60168f)),
    new MedianMouth(new float3(-200.41860f, 468.84370f, -886.17800f), new float3(-200.48270f, 468.84380f, -884.97960f), new float3(-4.86298f, -0.28896f, -0.26001f)),
    new[] { C(-186.75640f, 469.29870f, -898.06490f, -188.91800f, 469.27690f, -897.77840f, -191.09200f, 469.24250f, -897.59430f, -193.27090f, 469.17360f, -897.51100f), C(-185.20540f, 469.32320f, -886.16550f, -187.73000f, 469.30070f, -885.83170f, -190.26890f, 469.25640f, -885.61660f, -192.81360f, 469.17990f, -885.51950f), C(-185.20540f, 469.32320f, -886.16550f, -187.39150f, 469.30370f, -885.87640f, -189.23930f, 469.24030f, -884.47660f, -190.38530f, 469.19200f, -882.59250f), C(-183.65430f, 469.34780f, -874.26610f, -183.86670f, 469.34610f, -874.23800f, -184.07280f, 469.29900f, -874.16050f, -184.22870f, 469.25520f, -874.01350f) },
    new[] { C(-201.09100f, 468.84510f, -873.59590f, -200.90750f, 468.85420f, -873.58610f, -200.73010f, 468.94630f, -873.52000f, -200.59400f, 469.04100f, -873.39640f), C(-200.45070f, 468.84380f, -885.57880f, -198.27590f, 468.97290f, -885.46250f, -196.35430f, 469.03160f, -884.21420f, -195.09350f, 469.05730f, -882.43850f), C(-200.45070f, 468.84380f, -885.57880f, -197.90720f, 468.99480f, -885.44270f, -195.35890f, 469.10350f, -885.42220f, -192.81360f, 469.18000f, -885.51950f), C(-199.81030f, 468.84240f, -897.56170f, -197.63240f, 469.00140f, -897.44530f, -195.45030f, 469.10470f, -897.42760f, -193.27090f, 469.17360f, -897.51090f) },
    true);
CheckLogged("Alyssa Street junction", alyssaStreet,
    new MedianMouth(new float3(-198.62300f, 465.32100f, -935.86470f), new float3(-198.67530f, 465.32100f, -937.06360f), new float3(6.16204f, 0.18930f, -0.26828f)),
    new MedianMouth(new float3(-181.34390f, 465.72570f, -936.61710f), new float3(-181.39610f, 465.72570f, -937.81600f), new float3(6.49030f, 0.11084f, -0.28256f)),
    new[] { C(-198.12710f, 465.32100f, -924.47550f, -195.24730f, 465.40950f, -924.60090f, -192.36740f, 465.48610f, -924.72630f, -189.48760f, 465.55280f, -924.85170f), C(-198.64920f, 465.32100f, -936.46410f, -195.76930f, 465.40950f, -936.58950f, -192.88950f, 465.48610f, -936.71490f, -190.00960f, 465.55280f, -936.84030f), C(-198.64920f, 465.32100f, -936.46410f, -196.30900f, 465.39290f, -936.56600f, -194.22060f, 465.52520f, -937.87890f, -192.87610f, 465.60200f, -939.79690f), C(-199.17120f, 465.32100f, -948.45280f, -198.96100f, 465.32740f, -948.46200f, -198.75810f, 465.35790f, -948.54000f, -198.60740f, 465.38100f, -948.68680f) },
    new[] { C(-181.89210f, 465.72570f, -949.20530f, -182.02690f, 465.72340f, -949.19940f, -182.15460f, 465.66470f, -949.26010f, -182.24510f, 465.59610f, -949.36020f), C(-181.37000f, 465.72570f, -937.21660f, -183.64730f, 465.68690f, -937.11740f, -185.77530f, 465.71700f, -938.23590f, -187.22020f, 465.72800f, -939.99890f), C(-181.37000f, 465.72570f, -937.21660f, -184.24990f, 465.67660f, -937.09110f, -187.12970f, 465.61960f, -936.96580f, -190.00960f, 465.55280f, -936.84030f), C(-180.84800f, 465.72570f, -925.22790f, -183.72790f, 465.67660f, -925.10250f, -186.60770f, 465.61960f, -924.97710f, -189.48760f, 465.55280f, -924.85170f) },
    false);

var tight = Curve(0, 0, 0, 0.3f, 0.5f, 0.6f, 1f, 0.6f);
var tightRibbon = new MedianRibbon(tight, Mouth(tight, false, 2.1f), Mouth(tight, true, 2.1f), 2.1f);
var samples = new List<MedianSection>();
Check(!tightRibbon.Sample(Mouth(tight, false, 2.1f), Mouth(tight, true, 2.1f), samples, out _),
    "an offset whose inside radius folds must fail");
var malformed = new MedianMouth(new float3(0, 0, 1), new float3(0, 0, -1), new float3(0, 0, 1));
var flatRibbon = new MedianRibbon(straight, Mouth(straight, false, 1f), Mouth(straight, true, 1f), 1f);
Check(!flatRibbon.Sample(malformed, Mouth(straight, true, 1f), samples, out _), "misaligned native mouths fail");
Check(flatRibbon.Sample(Mouth(straight, false, 1f), Mouth(straight, true, 1f), samples, out _), "straight ribbon");
Check(!MedianRibbon.ClearsLane(samples, Shift(straight, 2.2f), 1.5f), "adjacent lane intrusion is detected");
Check(!MedianRibbon.ClearsLane(samples, Curve(-6, 0, -2, 8, 2, 16, 6, 24), 1.5f), "crossing intersects the strip");
Check(MedianRibbon.ClearsLane(samples, Shift(straight, 2.6f), 1.5f), "clear lane is accepted");
var reversedSections = new List<MedianSection>(samples);
var swapped = reversedSections[reversedSections.Count / 2];
reversedSections[reversedSections.Count / 2] = new MedianSection(swapped.Time, swapped.Center, -swapped.Across,
    swapped.Direction, swapped.Curvature);
Check(!MedianRibbon.ValidStrip(reversedSections), "a reversed cross-section makes degenerate or flipped faces");
var blocked = new MedianRibbon(asymmetric, Mouth(asymmetric, false, 2.1f), Mouth(asymmetric, true, 2.1f), 2.1f);
Check(blocked.Sample(Mouth(asymmetric, false, 2.1f), Mouth(asymmetric, true, 2.1f), samples, out _),
    "a fixed-width strip can be valid in isolation");
Check(!MedianRibbon.ClearsLane(samples, Shift(asymmetric, 4f), 1.5f),
    "the same strip can be too wide for a skewed lane corridor");

// The lattice of the strip's top layer, from a lane piece like the game's:
// 3 m wide, 64 m long, a row of vertices every 4 m, one quad across.
CheckLattice();

// The through-lane strip over the two closed junctions of a test city (with the grass upgrade),
// whose medians are 4.2 m wide. Each takes the side street's side of both
// through halves as fitted, and the side street as joined to the median.
var syncLeft = new float4(0f, 0.25833f, 0.29167f, 0.79167f);
var syncRight = new float4(0.20833f, 0.70833f, 0.74167f, 1f);
var beechwoodStrip = new StripFixture("Beechwood Street strip",
    C(-185.20540f, 469.32320f, -886.16550f, -190.25780f, 469.27800f, -885.49740f, -195.36160f, 469.14610f, -885.30690f, -200.45070f, 468.84370f, -885.57890f),
    new MedianMouth(new float3(-185.47680f, 469.31890f, -888.24800f), new float3(-184.93390f, 469.32750f, -884.08310f), new float3(-4.54989f, -0.04063f, 0.60168f)),
    new MedianMouth(new float3(-200.33860f, 468.84340f, -887.67600f), new float3(-200.56280f, 468.84390f, -883.48190f), new float3(-4.86298f, -0.28897f, -0.25995f)),
    new[] { C(-185.20540f, 469.32320f, -886.16550f, -188.29530f, 469.29550f, -885.78830f, -191.41740f, 469.25080f, -885.54890f, -194.57180f, 469.16640f, -885.48060f), C(-183.65430f, 469.34770f, -874.26610f, -183.86680f, 469.34600f, -874.23800f, -184.07280f, 469.29890f, -874.16060f, -184.22870f, 469.25510f, -874.01350f) },
    new float4(0.21141f, 0.72710f, 0.79830f, 1f),
    new[] { C(-201.09110f, 468.84510f, -873.59600f, -200.90750f, 468.85410f, -873.58620f, -200.73020f, 468.94620f, -873.52010f, -200.59410f, 469.04100f, -873.39650f), C(-200.45070f, 468.84370f, -885.57890f, -197.32890f, 469.02580f, -885.44320f, -194.19500f, 469.13910f, -885.44430f, -191.03660f, 469.21260f, -885.61550f) },
    new float4(0f, 0.20093f, 0.27160f, 0.78845f), syncLeft, syncRight,
    new[] { C(-184.40930f, 469.20210f, -873.59610f, -184.41550f, 469.19620f, -873.75480f, -184.34430f, 469.22270f, -873.90480f, -184.22870f, 469.25520f, -874.01370f), C(-192.40350f, 469.20190f, -873.29160f, -192.52890f, 469.08310f, -876.52970f, -192.63780f, 469.16600f, -880.73710f, -192.73500f, 469.18210f, -883.44420f), C(-192.40350f, 469.20190f, -873.29160f, -192.52910f, 469.08300f, -876.53390f, -192.63780f, 469.16600f, -880.73710f, -192.73500f, 469.18210f, -883.44420f), C(-200.39780f, 469.20170f, -872.98720f, -200.40390f, 469.19600f, -873.14450f, -200.47750f, 469.12200f, -873.29060f, -200.59410f, 469.04090f, -873.39640f) },
    new float4(0f, 0.24525f, 0.33183f, 1f), new float4(0f, 0.66971f, 0.75565f, 1f));
beechwoodStrip.ExpectedSettle = new float2(0.210f, 0.071f);
var alyssaStrip = new StripFixture("Alyssa Street strip", alyssaStreet,
    new MedianMouth(new float3(-198.55780f, 465.32100f, -934.36610f), new float3(-198.74050f, 465.32100f, -938.56220f), new float3(6.16204f, 0.18930f, -0.26828f)),
    new MedianMouth(new float3(-181.27870f, 465.72570f, -935.11850f), new float3(-181.46140f, 465.72570f, -939.31460f), new float3(6.49030f, 0.11084f, -0.28256f)),
    new[] { C(-198.64920f, 465.32100f, -936.46410f, -195.20260f, 465.42680f, -936.60480f, -191.75840f, 465.51310f, -936.75440f, -188.33330f, 465.58850f, -936.90030f), C(-199.17120f, 465.32100f, -948.45280f, -198.96100f, 465.32740f, -948.46200f, -198.75810f, 465.35790f, -948.54000f, -198.60740f, 465.38100f, -948.68680f) },
    new float4(0.20476f, 0.73101f, 0.80125f, 1f),
    new[] { C(-181.89210f, 465.72570f, -949.20530f, -182.02690f, 465.72340f, -949.19940f, -182.15460f, 465.66470f, -949.26010f, -182.24510f, 465.59610f, -949.36020f), C(-181.37000f, 465.72570f, -937.21660f, -184.83170f, 465.66660f, -937.05570f, -188.29350f, 465.61010f, -936.90260f, -191.73870f, 465.54380f, -936.75190f) },
    new float4(0f, 0.19660f, 0.26554f, 0.79458f), syncLeft, syncRight,
    new[] { C(-198.41610f, 465.39420f, -949.15380f, -198.40850f, 465.41090f, -948.97810f, -198.48140f, 465.40030f, -948.80950f, -198.60740f, 465.38100f, -948.68680f), C(-190.42370f, 465.39420f, -949.50180f, -190.27460f, 465.72040f, -946.07820f, -190.20400f, 465.61730f, -941.65690f, -190.10050f, 465.55290f, -938.92760f), C(-190.42370f, 465.39420f, -949.50180f, -190.27450f, 465.72060f, -946.07540f, -190.20400f, 465.61730f, -941.65690f, -190.10050f, 465.55290f, -938.92760f), C(-182.43130f, 465.39420f, -949.84990f, -182.42340f, 465.41130f, -949.67020f, -182.36570f, 465.50470f, -949.49380f, -182.24510f, 465.59610f, -949.36020f) },
    new float4(0f, 0.24003f, 0.32487f, 1f), new float4(0f, 0.67908f, 0.76240f, 1f));
alyssaStrip.ExpectedSettle = new float2(-0.183f, -0.061f);
foreach (int diagonal in new[] { 1, -1, 0 })
{
    CheckStrip(beechwoodStrip, diagonal, 0.043f, new float2(-0.08615f, 0.08765f));
    CheckStrip(alyssaStrip, diagonal, 0.020f, new float2(-0.08304f, 0.06647f));
}
// The curved and the straight junction share one side-street mesh, so the
// strips' steps must not depend on the bend. The inner steps stay on the
// skirt beside the strip's reach over it.
{
    const int steps = 8;
    const float pieceHalfWidth = 2.5f;
    float skirt = pieceHalfWidth - beechwoodStrip.Ribbon.HalfWidth;
    float tread = NodeSurface.InnerStepTread(skirt, steps, 0.06f);
    Check(tread == NodeSurface.InnerStepTread(pieceHalfWidth - alyssaStrip.Ribbon.HalfWidth, steps, 0.06f) && tread > 0.02f,
        "both logged junctions give their strips the same inner steps");
    foreach (var fixture in new[] { beechwoodStrip, alyssaStrip, beechwoodStrip.Crest(0.05f) })
        Check(NodeSurface.SkirtReach(fixture.Ribbon, LaneLattice(1), skirt) + steps * tread <= skirt + 0.0001f,
            $"{fixture.Name}: the inner steps stay on the divider's skirt");
}
// The same junctions with the side street on the ribbon's left.
CheckMirror(beechwoodStrip);
CheckMirror(alyssaStrip);
// The same junctions on a crest and in a sag, where the cross-section bends
// between the strip's rows of vertices.
foreach (float grade in new[] { 0.03f, -0.03f, 0.05f, -0.05f })
{
    CheckCrest(alyssaStrip, grade);
    CheckCrest(beechwoodStrip, grade);
}
CheckLayers();
CheckMouths();

Console.WriteLine("Five geometry fixtures, the two logged Finchale junctions, their through-lane strips and the rejection/clearance checks passed.");

static void CheckRibbon(string name, Bezier4x3 guide, float halfWidth, float roadHalfWidth, float cornerFlare)
{
    var start = Mouth(guide, false, halfWidth);
    var end = Mouth(guide, true, halfWidth);
    var center = MedianRibbon.FitCenter(start, end, guide);
    var ribbon = new MedianRibbon(center, start, end, halfWidth);
    var sections = new List<MedianSection>();
    Check(ribbon.Sample(start, end, sections, out var reason), $"{name}: {reason}");
    CheckRenderRails(name, ribbon, roadHalfWidth);

    MathUtils.Divide(center, out var a, out var b, 0.5f);
    b = MathUtils.Invert(b);
    float fraction = halfWidth / roadHalfWidth;
    var near = new NativeCurbFit.RailPair(ribbon, a, Outside(a, roadHalfWidth, cornerFlare), fraction,
        a, Outside(a, -roadHalfWidth, -cornerFlare), fraction, false);
    var far = new NativeCurbFit.RailPair(ribbon, b, Outside(b, -roadHalfWidth, -cornerFlare * 0.5f), fraction,
        b, Outside(b, roadHalfWidth, cornerFlare * 0.5f), fraction, true);
    NativeCurbFit.Fit(ribbon, near, far, out var aLeft, out var aRight, out var bLeft, out var bRight);
    var startDirection = math.normalizesafe(start.Tangent.xz);
    var endDirection = math.normalizesafe(end.Tangent.xz);
    Check(aLeft.Joins(bLeft, start.Left, end.Left, startDirection, endDirection) &&
        aRight.Joins(bRight, start.Right, end.Right, startDirection, endDirection), $"{name}: runtime join checks");
    Check(math.distance(aLeft.Curb(1f), bLeft.Curb(1f)) < 0.0001f, $"{name}: continuous left seam");
    Check(math.distance(aRight.Curb(1f), bRight.Curb(1f)) < 0.0001f, $"{name}: continuous right seam");
    Check(math.distance(aLeft.Curb(0f), start.Left) < 0.0001f &&
        math.distance(aRight.Curb(0f), start.Right) < 0.0001f &&
        math.distance(bLeft.Curb(0f), end.Left) < 0.0001f &&
        math.distance(bRight.Curb(0f), end.Right) < 0.0001f, $"{name}: native curb joins");
    Check(math.dot(math.normalize((aLeft.Curb(0.001f) - aLeft.Curb(0f)).xz),
        math.normalize(start.Tangent.xz)) > 0.999f, $"{name}: native mouth tangent");
    Check(math.dot(math.normalize(aLeft.Curb(1f) - aLeft.Curb(0.999f)),
        math.normalize(bLeft.Curb(0.999f) - bLeft.Curb(1f))) > 0.999f, $"{name}: shared seam tangent");
    var middle = ribbon.Section(0.5f);
    float2 normal = middle.Across.xz / halfWidth;
    CheckProfile(aLeft, normal);
    CheckProfile(bLeft, normal);
    CheckProfile(aRight, -normal);
    CheckProfile(bRight, -normal);

    float maxWidthError = 0f;
    foreach (var section in sections)
    {
        Check(math.abs(math.length(section.Across.xz) - halfWidth) < 0.0001f, $"{name}: fixed offset");
        Check(math.abs(math.dot(section.Across.xz, section.Direction)) < 0.0001f, $"{name}: normal cross-section");
        bool first = section.Time <= 0.5f;
        float t = (first ? section.Time : 1f - section.Time) * 2f;
        float2 left = (first ? aLeft : bLeft).Curb(t).xz;
        float2 right = (first ? aRight : bRight).Curb(t).xz;
        maxWidthError = math.max(maxWidthError, math.abs(math.distance(left, right) - 2f * halfWidth));
    }
    Check(maxWidthError <= MedianRibbon.WidthTolerance, $"{name}: native width error {maxWidthError:F3} m");

    // Between the mouth and the center the pavement marker follows the fitted
    // inner rail, so the pavement edge can leave the rigid skirt edge slightly.
    const float skirtWidth = 0.4f;
    float markerScale = (halfWidth + skirtWidth) / halfWidth;
    var skirt = NativeCurbFit.SkirtRange(ribbon, sections, aLeft, aRight, bLeft, bRight, markerScale, markerScale);
    float maxSkirtError = math.cmax(math.abs(skirt));
    Check(maxSkirtError <= MedianRibbon.JoinTolerance,
        $"{name}: pavement edge leaves the divider skirt by {maxSkirtError:F3} m");

    Console.WriteLine($"{name}: {sections.Count} sections, {maxWidthError:F3} m maximum modeled render width error, " +
        $"{maxSkirtError:F3} m maximum modeled pavement-to-skirt error");
    CheckTuck(name, ribbon, sections, markerScale, skirtWidth, new[] { a, a, b, b }, inner =>
    {
        var nearPair = new NativeCurbFit.RailPair(ribbon, inner[0], Outside(a, roadHalfWidth, cornerFlare), fraction,
            inner[1], Outside(a, -roadHalfWidth, -cornerFlare), fraction, false);
        var farPair = new NativeCurbFit.RailPair(ribbon, inner[2], Outside(b, -roadHalfWidth, -cornerFlare * 0.5f), fraction,
            inner[3], Outside(b, roadHalfWidth, cornerFlare * 0.5f), fraction, true);
        NativeCurbFit.Fit(ribbon, nearPair, farPair, out var nearLeft, out var nearRight, out var farLeft, out var farRight);
        return new[] { nearLeft, nearRight, farLeft, farRight };
    }, null);

    void CheckProfile(NativeCurbFit fit, float2 outward)
    {
        // A vertex just inside the curb marker must retain its native lateral
        // distance from the face at the center, where the old mesh collapsed it.
        const float tread = 0.12f;
        float3 inside = math.lerp(fit.Curb(1f), fit.Inner.d, tread / halfWidth);
        Check(math.abs(math.dot((fit.Curb(1f) - inside).xz, outward) - tread) < 0.0001f,
            $"{name}: curb tread retains width at the center");
        // The lane pavement begins at the divider piece's outer edge. A marker
        // there must keep that edge one skirt width from the curb at the center.
        const float skirt = 0.4f;
        float scale = (halfWidth + skirt) / halfWidth;
        Check(math.abs(math.dot((fit.Marker(1f, scale) - fit.Curb(1f)).xz, outward) - skirt) < 0.0001f,
            $"{name}: pavement edge stays at the divider skirt at the center");
        Check(math.distance(fit.Marker(1f, 1f), fit.Curb(1f)) < 0.0001f &&
            math.distance(fit.Marker(0.4f, 1f), fit.Curb(0.4f)) < 0.0001f,
            $"{name}: a piece without a skirt keeps the marker on the curb");
    }
}

// Replays the fit for a junction the game logged. The rails are the left outer,
// left inner, right inner and right outer rail of each approach, and the pairs
// are built from them the way AlignMedianSystem.TryAlign builds them.
static void CheckLogged(string name, Bezier4x3 center, MedianMouth first, MedianMouth second,
    Bezier4x3[] near, Bezier4x3[] far, bool gapBeforeTuck)
{
    const float halfWidth = 0.60001f;
    const float pieceHalfWidth = 1f;
    const float roadHalfWidth = 12f;
    float fraction = halfWidth / roadHalfWidth;
    var ribbon = new MedianRibbon(center, first, second, halfWidth);
    var sections = new List<MedianSection>();
    Check(ribbon.Sample(first, second, sections, out var reason), $"{name}: {reason}");
    // The fits that come back as near left, near right, far left and far right
    // are stored in the near approach's left and right inner rails and in the
    // far approach's right and left inner rails.
    CheckTuck(name, ribbon, sections, pieceHalfWidth / halfWidth, pieceHalfWidth - halfWidth,
        new[] { near[1], near[2], far[2], far[1] }, inner =>
    {
        var nearPair = new NativeCurbFit.RailPair(ribbon, inner[0], near[0], fraction, inner[1], near[3], fraction, false);
        var farPair = new NativeCurbFit.RailPair(ribbon, inner[2], far[3], fraction, inner[3], far[0], fraction, true);
        NativeCurbFit.Fit(ribbon, nearPair, farPair, out var nearLeft, out var nearRight, out var farLeft, out var farRight);
        Check(nearLeft.Joins(farLeft, first.Left, second.Left, sections[0].Direction, sections[sections.Count - 1].Direction) &&
            nearRight.Joins(farRight, first.Right, second.Right, sections[0].Direction, sections[sections.Count - 1].Direction),
            $"{name}: the fit meets both native mouths");
        return new[] { nearLeft, nearRight, farLeft, farRight };
    }, gapBeforeTuck);
}

// A divider on its own rails has no pavement under its skirt, and it is cut
// into six straight segments in each half where the deformed pavement has eight.
// Checks the tuck that covers the difference, as the two meshes are cut.
static void CheckTuck(string name, MedianRibbon ribbon, List<MedianSection> sections, float scale, float skirtWidth,
    Bezier4x3[] nativeInner, Func<Bezier4x3[], NativeCurbFit[]> fit, bool? gapBeforeTuck)
{
    var fits = fit(nativeInner);
    float before = Cover(ribbon, fits, scale);
    if (gapBeforeTuck == true)
        Check(before > 0.002f, $"{name}: the untucked pavement leaves a gap of only {before:F4} m");
    else if (gapBeforeTuck == false)
        Check(before < 0.001f, $"{name}: the untucked pavement leaves a gap of {before:F4} m");

    float tuck = NativeCurbFit.SkirtTuck(ribbon, sections, fits[0], fits[1], fits[2], fits[3], scale, scale);
    Check(tuck >= NativeCurbFit.MinimumTuck && tuck <= NativeCurbFit.MaximumTuck, $"{name}: tuck {tuck:F3} m");
    Check(NativeCurbFit.SkirtTuck(ribbon, sections, fits[0], fits[1], fits[2], fits[3], 1f, 1f) == 0f,
        $"{name}: a divider without a skirt is not tucked");
    var tucked = Tuck(ribbon, fits, tuck);
    float after = Cover(ribbon, tucked, scale);
    // A tuck at its limit covers what it can. Below the limit nothing may show.
    Check(after < before || after <= 0.0005f, $"{name}: the tuck widens the gap from {before:F4} to {after:F4} m");
    Check(tuck >= NativeCurbFit.MaximumTuck || after <= 0.0005f,
        $"{name}: the tucked pavement still leaves a gap of {after:F4} m");
    Check(Cover(ribbon, Tuck(ribbon, fits, -tuck), scale) > math.max(before, 0.0005f) + tuck * 0.25f,
        $"{name}: a tuck in the wrong direction must open a gap");
    var range = NativeCurbFit.SkirtRange(ribbon, sections, tucked[0], tucked[1], tucked[2], tucked[3], scale, scale);
    Check(range.x >= -skirtWidth * 0.5f, $"{name}: the tucked pavement edge reaches {-range.x:F4} m under the skirt");

    // The node mesh must still meet the road mesh at the mouth.
    for (int i = 0; i < 4; i++)
        Check(math.distance(tucked[i].Marker(0f, scale), fits[i].Marker(0f, scale)) < 0.00001f &&
            math.distance(tucked[i].Curb(0f), fits[i].Curb(0f)) < 0.00001f,
            $"{name}: the tuck moves the mouth");

    // The game recomputes only the roads it updates. A fit that reads its own
    // tucked rails back must return the same rails, however often it runs.
    var stored = tucked;
    for (int pass = 1; pass <= 4; pass++)
    {
        var again = fit(new[] { stored[0].Inner, stored[1].Inner, stored[2].Inner, stored[3].Inner });
        float repeated = NativeCurbFit.SkirtTuck(ribbon, sections, again[0], again[1], again[2], again[3], scale, scale);
        Check(math.abs(repeated - tuck) < 0.00001f, $"{name}: pass {pass} changes the tuck from {tuck:F4} to {repeated:F4} m");
        var next = Tuck(ribbon, again, repeated);
        for (int i = 0; i < 4; i++)
        {
            float drift = math.cmax(new float4(math.distance(next[i].Inner.a, tucked[i].Inner.a),
                math.distance(next[i].Inner.b, tucked[i].Inner.b), math.distance(next[i].Inner.c, tucked[i].Inner.c),
                math.distance(next[i].Inner.d, tucked[i].Inner.d)));
            Check(drift < 0.00001f, $"{name}: pass {pass} moves a tucked rail by {drift:F5} m");
        }
        stored = next;
    }
    Console.WriteLine($"{name}: gap {before * 1000f:F1} mm as cut before the tuck, {tuck * 1000f:F1} mm tuck, " +
        $"{(after > 0f ? "gap " : "cover ")}{math.abs(after) * 1000f:F1} mm at the tightest point after it");
}

static NativeCurbFit[] Tuck(MedianRibbon ribbon, NativeCurbFit[] fits, float tuck) => new[]
{
    fits[0].Tucked(ribbon, false, true, tuck), fits[1].Tucked(ribbon, false, false, tuck),
    fits[2].Tucked(ribbon, true, true, tuck), fits[3].Tucked(ribbon, true, false, tuck)
};

// Largest distance by which the pavement's inner edge lies outside the rigid
// divider's skirt edge, with both drawn as the straight segments of their
// meshes. Past the mouths a negative value means the pavement is covered.
static float Cover(MedianRibbon ribbon, NativeCurbFit[] fits, float scale)
{
    float worst = float.NegativeInfinity;
    for (int half = 0; half < 2; half++)
    {
        bool far = half == 1;
        var rails = new MedianRenderRails(ribbon, far);
        var left = fits[far ? 2 : 0];
        var right = fits[far ? 3 : 1];
        for (int i = 6; i <= 240; i++)
        {
            float t = i / 240f;
            Skirt(rails, far, scale, math.min(5, (int)(t * 6f)), out var skirtLeft, out var skirtRight);
            Skirt(rails, far, scale, math.min(5, (int)(t * 6f)) + 1, out var nextLeft, out var nextRight);
            float along = t * 6f - math.min(5, (int)(t * 6f));
            int segment = math.min(7, (int)(t * 8f));
            float between = t * 8f - segment;
            float3 pavementLeft = math.lerp(left.Marker(segment / 8f, scale), left.Marker((segment + 1) / 8f, scale), between);
            float3 pavementRight = math.lerp(right.Marker(segment / 8f, scale), right.Marker((segment + 1) / 8f, scale), between);
            var section = ribbon.Section(far ? 1f - t * 0.5f : t * 0.5f);
            float2 normal = section.Across.xz / ribbon.HalfWidth;
            worst = math.max(worst, math.dot((pavementLeft - math.lerp(skirtLeft, nextLeft, along)).xz, normal));
            worst = math.max(worst, -math.dot((pavementRight - math.lerp(skirtRight, nextRight, along)).xz, normal));
        }
    }
    return worst;
}

static void Skirt(MedianRenderRails rails, bool far, float scale, int vertex, out float3 left, out float3 right)
{
    rails.Sample(vertex / 6f, out var a, out var b);
    if (far) (a, b) = (b, a);
    left = (a + b) * 0.5f + (a - b) * (0.5f * scale);
    right = (a + b) * 0.5f - (a - b) * (0.5f * scale);
}

// A lattice like the game's from a 3 m lane piece with a row of vertices every
// 4 m, two lanes across a 6 m band of a side street's junction mesh. Its quads
// are split along the diagonal named, or the diagonals are left unknown (0).
static NodeSurface.Lattice LaneLattice(int diagonal, float width = 16f, float halfLength = 32f)
{
    var points = new List<float3>();
    var triangles = new List<int>();
    for (float z = -halfLength; z <= halfLength + 0.001f; z += 4f)
    {
        points.Add(new float3(-1.5f, -0.2f, z));
        points.Add(new float3(1.5f, -0.2f, z));
    }
    for (int j = 0; j + 3 < points.Count; j += 2)
    {
        if (diagonal >= 0) triangles.AddRange(new[] { j, j + 1, j + 3, j, j + 3, j + 2 });
        else triangles.AddRange(new[] { j, j + 1, j + 2, j + 1, j + 3, j + 2 });
    }
    var vertices = points.ToArray();
    var indices = triangles.ToArray();
    var lattice = new NodeSurface.Lattice();
    Check(lattice.Build(6f, width, halfLength, new[] { -1.5f, 1.5f }, new[] { vertices, vertices },
        new[] { indices, indices }), "a lane lattice is built");
    if (diagonal == 0) for (int i = 0; i < lattice.Diagonals.Count; i++) lattice.Diagonals[i] = 0;
    return lattice;
}

static bool SameKnots(List<float> knots, params float[] expected)
{
    if (knots.Count != expected.Length) return false;
    for (int i = 0; i < knots.Count; i++) if (math.abs(knots[i] - expected[i]) > 0.0001f) return false;
    return true;
}

static void CheckLattice()
{
    var lattice = LaneLattice(1);
    Check(SameKnots(lattice.Rows, 0f, 0.25f, 0.5f, 0.75f, 1f) && SameKnots(lattice.Columns, 0f, 0.5f, 1f),
        "a 16 m wide side street draws the strip in rows 4 m apart, with columns at the lane edges");
    Check(lattice.Diagonals.TrueForAll(d => d == 1),
        "a quad split from its low corner to its high one along the piece is split the same way along the strip");
    Check(LaneLattice(-1).Diagonals.TrueForAll(d => d == -1), "the other split gives the other diagonal");
    var narrow = LaneLattice(1, 14f);
    Check(SameKnots(narrow.Rows, 0f, 4f / 14f, 8f / 14f, 12f / 14f, 1f),
        "a 14 m wide side street has rows 4 m apart and a short last row");
    Check(SameKnots(LaneLattice(1, 10f).Rows, 0f, 0.4f, 0.8f, 1f), "a 10 m wide side street");
    Check(SameKnots(LaneLattice(1, 16f, 4f).Rows, 0f, 1f), "a lane piece shorter than the side street spans the rails");
    narrow.Turn();
    Check(SameKnots(narrow.Rows, 0f, 2f / 14f, 6f / 14f, 10f / 14f, 1f) && narrow.Diagonals.TrueForAll(d => d == -1),
        "rails that run the other way reverse the rows and the diagonals");

    // The drawn strip is flat on each triangle. In the cell from 0.5 to 1
    // across and 0 to 0.25 along, a lift of the outer edge alone twists it.
    var flat = new MedianRibbon(Curve(0, 0, 0, 8, 0, 16, 0, 24), Mouth(Curve(0, 0, 0, 8, 0, 16, 0, 24), false, 1f),
        Mouth(Curve(0, 0, 0, 8, 0, 16, 0, 24), true, 1f), 1f);
    lattice.Prepare(flat, 1f, 1.5f, 7.5f);
    float4 inner = 0f, outer = new float4(0f, 0.3f, 0f, 0f);
    float high = 3f * 0.75f * 0.75f * 0.25f * 0.3f;
    Check(math.abs(lattice.Lift(inner, outer, 0.75f, 0.125f) - high * 0.5f) < 0.0001f &&
        math.abs(lattice.Lift(inner, outer, 0.75f, 0.125f, -1) - high * 0.25f) < 0.0001f,
        "a twisted cell is drawn along its own diagonal");
    var unknown = LaneLattice(0);
    unknown.Prepare(flat, 1f, 1.5f, 7.5f);
    Check(math.abs(unknown.Lift(inner, outer, 0.75f, 0.125f) - high * 0.25f) < 0.0001f,
        "without a known diagonal the lower triangulation counts");
    Check(math.abs(lattice.Lift(inner, outer, 1f, 0.25f) - high) < 0.0001f &&
        math.abs(lattice.Lift(inner, outer, 0.5f, 0.25f) - high * 0.5f) < 0.0001f,
        "vertices carry the cubic's own heights");
}

const float StripInner = 2.5f, StripOuter = 8.5f, StripMargin = 0.012f, HalfRoad = 12f;

static float StripEdge(MedianRibbon ribbon, NodeSurface.Lattice lattice) =>
    StripInner - NodeSurface.SkirtReach(ribbon, lattice, StripInner - ribbon.HalfWidth);

// The pavement under the strip, as MapStrip collects it, with the halves of
// the through road that the production code picks for this side.
static void Collect(StripFixture f, MedianRibbon ribbon, float side, float edge, NodeSurface.Lattice lattice,
    List<NodeSurface.Sample> samples, int across = 24, int along = 48)
{
    Check(NodeSurface.ThroughLeftHalf(side, false) == f.FirstIsLeft && NodeSurface.ThroughLeftHalf(side, true) == f.SecondIsLeft,
        $"{f.Name}: the halves of the through road on the side street's side are picked");
    var offsets = new float4(0f, 0.375f, 0.625f, 1f);
    samples.Clear();
    NodeSurface.Through(ribbon, side, edge, StripOuter, lattice, f.First[0], f.First[1], f.FirstOffsets, f.FirstTargets,
        HalfRoad, f.FirstIsLeft, samples, across, along);
    NodeSurface.Through(ribbon, side, edge, StripOuter, lattice, f.Second[0], f.Second[1], f.SecondOffsets, f.SecondTargets,
        HalfRoad, f.SecondIsLeft, samples, across, along);
    NodeSurface.Rise(ribbon, side, edge, StripOuter, lattice, f.Street[0], f.Street[1], offsets, f.StreetLeft, 0f, 1f, 1,
        samples, across, along);
    NodeSurface.Rise(ribbon, side, edge, StripOuter, lattice, f.Street[2], f.Street[3], offsets, f.StreetRight, 0f, 1f, -1,
        samples, across, along);
}

// Seats the strip as MapStrip does. The lattice is turned when the rails are.
static bool SeatStrip(StripFixture f, float side, NodeSurface.Lattice lattice, bool settle,
    List<NodeSurface.Sample> samples, out NodeSurface.Seating seating, out float edge, out bool turned)
{
    var ribbon = f.Ribbon;
    edge = StripEdge(ribbon, lattice);
    turned = NodeSurface.Turns(ribbon, side, edge, StripOuter, false);
    if (turned) lattice.Turn();
    lattice.Prepare(ribbon, side, edge, StripOuter);
    Collect(f, ribbon, side, edge, lattice, samples);
    return NodeSurface.Seat(samples, lattice, StripMargin, 0.3f, settle, f.Grade, out seating);
}

// Least height of the drawn strip above the pavement under it, on a grid seven
// times as dense as the one the strip was seated on.
static float DrawnClearance(StripFixture settled, float side, float edge, NodeSurface.Lattice lattice,
    NodeSurface.Seating seating)
{
    var dense = new List<NodeSurface.Sample>();
    Collect(settled, settled.Ribbon, side, edge, lattice, dense, 64, 128);
    float clearance = float.PositiveInfinity;
    foreach (var sample in dense)
        clearance = math.min(clearance, lattice.Lift(seating.Inner, seating.Outer, sample.Corners, sample.Weights) - sample.Rise);
    return clearance;
}

// A strip on the right of the ribbon over a side street, with the lane
// piece's quads split along one diagonal, along the other, or unknown.
static void CheckStrip(StripFixture f, int diagonal, float loggedOverlap, float2 loggedRise)
{
    const float side = -1f;
    string name = $"{f.Name} ({(diagonal > 0 ? "main diagonal" : diagonal < 0 ? "other diagonal" : "diagonal unknown")})";
    var ribbon = f.Ribbon;
    var lattice = LaneLattice(diagonal);
    var samples = new List<NodeSurface.Sample>();
    bool plainFits = SeatStrip(f, side, lattice, false, samples, out var plain, out float edge, out bool turned);
    Check(math.abs(StripInner - edge - loggedOverlap) < 0.001f, $"{name}: the fixture has the logged reach over the skirt");
    Check(!turned && NodeSurface.Turns(ribbon, side, edge, StripOuter, true),
        $"{name}: a strip on the ribbon's right keeps the rails' direction unless the diagnostic switch turns it");
    // The log was written with 16 by 32 samples per junction half.
    var logged = new List<NodeSurface.Sample>();
    Collect(f, ribbon, side, edge, lattice, logged, 16, 32);
    var rise = new float2(float.PositiveInfinity, float.NegativeInfinity);
    foreach (var sample in logged) rise = new float2(math.min(rise.x, sample.Height), math.max(rise.y, sample.Height));
    Check(math.cmax(math.abs(rise - loggedRise)) < 0.003f, $"{name}: the fixture has the logged pavement heights");

    // A smooth strip cannot follow the ridge or trough that the side street
    // leaves along its middle rail. Settling that rail brings the strip down.
    Check(plainFits && plain.Settle.Equals(float2.zero), $"{name}: the strip seats on the pavement as the game laid it");
    Check(NodeSurface.Seat(samples, lattice, StripMargin, 0.3f, true, f.Grade, out var seating),
        $"{name}: the strip seats with the side street's middle rail");
    Check(seating.Floating < 0.08f && plain.Floating > seating.Floating + 0.02f,
        $"{name}: settling the middle rail brings the strip within 8 cm of the pavement");
    float4 corners = new float4(seating.Inner.x, seating.Inner.w, seating.Outer.x, seating.Outer.w);
    Check(math.cmin(corners) > StripMargin - 0.001f && math.cmax(corners) < 0.02f,
        $"{name}: the strip's ends lie within 2 cm of the road at both mouths");
    // The steps that end the strip's long edges reach 8 cm down on either
    // side, as far as an accepted strip may float.
    Check(seating.Edge < 0.08f && seating.Inside < 0.08f,
        $"{name}: the steps at the strip's edges reach the pavement ({seating.Edge * 100f:F2} and {seating.Inside * 100f:F2} cm)");
    // Regression values for the pull of the middle rail toward an even grade,
    // for quads split along the main diagonal.
    float2 expected = f.ExpectedSettle;
    Check(diagonal != 1 || math.cmax(math.abs(seating.Settle - expected)) < 0.01f,
        $"{name}: the middle rail settles by {seating.Settle * 100f} cm, as before ({expected * 100f} cm)");

    // The pavement as it lies once the rail is settled. The strip was seated
    // on a linear model of it, which must hold on the pavement itself, and
    // the drawn strip must stay above it between the samples too.
    var settled = f.Settled(seating.Settle);
    Collect(settled, ribbon, side, edge, lattice, samples);
    float clearance = float.PositiveInfinity;
    float height = 0f;
    foreach (var sample in samples)
    {
        float lift = lattice.Lift(seating.Inner, seating.Outer, sample.Corners, sample.Weights);
        clearance = math.min(clearance, lift - sample.Rise);
        height = math.max(height, math.abs(lift));
    }
    Check(math.abs(clearance - StripMargin) < 0.0003f, $"{name}: the strip clears the settled pavement by the margin");
    float drawn = DrawnClearance(settled, side, edge, lattice, seating);
    Check(drawn > StripMargin - 0.002f, $"{name}: the drawn strip stays {drawn * 1000f:F1} mm above the pavement on a dense grid");
    Console.WriteLine($"{name}: at most {seating.Floating * 100f:F1} cm above the pavement, {seating.Edge * 100f:F1} cm " +
        $"along its outer edge and {seating.Inside * 100f:F1} cm along its inner edge, against {plain.Floating * 100f:F1} cm " +
        $"without settling; ends {math.cmin(corners) * 100f:F1} to {math.cmax(corners) * 100f:F1} cm above the road; middle " +
        $"rail moved {seating.Settle.x * 100f:F1} and {seating.Settle.y * 100f:F1} cm; least clearance {drawn * 1000f:F1} mm");

    // The game hands back the pavement as it was written. Seating the strip
    // on its own result must leave both where they are.
    Check(NodeSurface.Seat(samples, lattice, StripMargin, 0.3f, true, settled.Grade, out var again) &&
        math.cmax(math.abs(again.Settle)) < 0.001f &&
        math.cmax(math.max(math.abs(again.Inner - seating.Inner), math.abs(again.Outer - seating.Outer))) < 0.001f,
        $"{name}: a second seating stays within a millimeter of the first");
    Check(!NodeSurface.Seat(samples, lattice, StripMargin, 0.01f, true, settled.Grade, out _),
        $"{name}: a strip that would leave the road's cross-section by more than the limit is refused");
    float moved = math.cmax(math.abs(seating.Settle));
    Check(moved > height + 0.01f, $"{name}: the middle rail moves more than the strip leaves the cross-section");
    Check(!NodeSurface.Seat(SeatSamples(f, side, lattice), lattice, StripMargin, (height + moved) * 0.5f, true, f.Grade, out _),
        $"{name}: a middle rail that would move by more than the limit is refused");

    // The rails carry the strip's vertices at the seated heights.
    NodeSurface.Rails(ribbon, side, edge, StripOuter, seating, lattice, false, out var innerRail, out var outerRail,
        out float drift);
    Check(drift < 0.03f, $"{name}: the strip's vertices lie within 3 cm of where it was seated ({drift * 100f:F1} cm)");
    for (int row = 0; row < lattice.Rows.Count; row++)
        for (int column = 0; column < lattice.Columns.Count; column++)
        {
            float time = lattice.Rows[row];
            float across = lattice.Columns[column];
            float3 point = math.lerp(MathUtils.Position(innerRail, time), MathUtils.Position(outerRail, time), across);
            float seated = ribbon.Offset(time, side * math.lerp(edge, StripOuter, across)).y +
                lattice.VertexLift(row * lattice.Columns.Count + column, seating.Inner, seating.Outer);
            Check(math.abs(point.y - seated) < 0.001f, $"{name}: the rails carry the vertices at the seated heights");
            Check(math.dot(MathUtils.Left(MathUtils.Tangent(innerRail, time).xz),
                (MathUtils.Position(innerRail, time) - MathUtils.Position(outerRail, time)).xz) > 0f,
                $"{name}: the inner edge lies on the left of the rails' direction");
        }
    // The game's placement of a mesh that keeps its shape, for the strip's
    // two edges: 3 m either side of the middle of a 16 m wide cross-section.
    const float width = 16f, halfBand = 3f;
    var origin = new float3(-190f, 467f, -900f);
    new MedianRenderRails(innerRail, outerRail).Pack(width, 0f, width, halfBand, origin,
        new float4(0f, 5f, 10f, 15f), out var left, out var right);
    foreach (float time in lattice.Rows)
    {
        var lower = At(left, time);
        var upper = At(right, time);
        Check(math.distance(math.lerp(lower, upper, 0.5f - halfBand / width).xyz + origin,
                MathUtils.Position(innerRail, time)) < 0.002f &&
            math.distance(math.lerp(lower, upper, 0.5f + halfBand / width).xyz + origin,
                MathUtils.Position(outerRail, time)) < 0.002f,
            $"{name}: the packed rails place the strip's edges on its rails");
    }
}

static List<NodeSurface.Sample> SeatSamples(StripFixture f, float side, NodeSurface.Lattice lattice)
{
    var samples = new List<NodeSurface.Sample>();
    Collect(f, f.Ribbon, side, StripEdge(f.Ribbon, lattice), lattice, samples);
    return samples;
}

// The junction reflected so that its side street lies on the ribbon's left.
// With the quads' diagonals unknown, the strip must seat the same way, on
// rails that run the other way along the ribbon.
static void CheckMirror(StripFixture f)
{
    string name = $"{f.Name}, mirrored";
    var mirrored = f.Mirror();
    var samples = new List<NodeSurface.Sample>();
    Check(SeatStrip(f, -1f, LaneLattice(0), true, samples, out var original, out float edge, out bool originalTurned),
        $"{name}: the original seats");
    var lattice = LaneLattice(0);
    Check(SeatStrip(mirrored, 1f, lattice, true, samples, out var seating, out float mirroredEdge, out bool turned),
        $"{name}: the mirror image seats");
    Check(!originalTurned && turned, $"{name}: the rails of a strip on the left run back along the ribbon");
    Check(math.abs(edge - mirroredEdge) < 0.0001f &&
        math.cmax(math.abs(new float4(seating.Floating, seating.Edge, seating.Inside, 0f) -
            new float4(original.Floating, original.Edge, original.Inside, 0f))) < 0.001f &&
        math.cmax(math.abs(seating.Settle - original.Settle)) < 0.001f &&
        math.cmax(math.max(math.abs(seating.Inner - original.Inner), math.abs(seating.Outer - original.Outer))) < 0.001f,
        $"{name}: the strip seats as it does on the right");
    NodeSurface.Rails(f.Ribbon, -1f, edge, StripOuter, original, LaneLattice(0), false, out var innerRail, out _, out _);
    NodeSurface.Rails(mirrored.Ribbon, 1f, edge, StripOuter, seating, lattice, false, out var mirroredInner, out _,
        out float drift);
    Check(drift < 0.03f, $"{name}: the strip's vertices lie where it was seated");
    for (int i = 0; i <= 8; i++)
    {
        float time = i / 8f;
        float3 point = MathUtils.Position(innerRail, 1f - time);
        Check(math.distance(MathUtils.Position(mirroredInner, time), new float3(-point.x, point.y, point.z)) < 0.002f,
            $"{name}: the mirrored rails are the original ones reflected and turned");
    }
    float drawn = DrawnClearance(mirrored.Settled(seating.Settle), 1f, mirroredEdge, lattice, seating);
    Check(drawn > StripMargin - 0.004f, $"{name}: the drawn strip stays {drawn * 1000f:F1} mm above the pavement");
}

// The junction on a crest (positive grade at the mouths, falling to them) or
// in a sag. Either the strip is refused or the drawn strip clears the pavement.
static void CheckCrest(StripFixture f, float grade)
{
    var bent = f.Crest(grade);
    string name = $"{bent.Name}";
    var lattice = LaneLattice(1);
    var samples = new List<NodeSurface.Sample>();
    bool fits = SeatStrip(bent, -1f, lattice, true, samples, out var seating, out float edge, out _);
    if (!fits || seating.Floating > 0.08f)
    {
        Console.WriteLine($"{name}: refused, {seating.Floating * 100f:F1} cm between strip and pavement");
        return;
    }
    float drawn = DrawnClearance(bent.Settled(seating.Settle), -1f, edge, lattice, seating);
    Check(drawn > StripMargin - 0.004f, $"{name}: the drawn strip stays {drawn * 1000f:F1} mm above the pavement");
    Console.WriteLine($"{name}: at most {seating.Floating * 100f:F1} cm above the pavement, least clearance {drawn * 1000f:F1} mm");
}

// Two layers of pavement under the strip: the road's own cross-section and a
// layer that rises 5 cm above it between the mouths. The strip follows the
// upper one, which is the one in sight.
static void CheckLayers()
{
    var straight = Curve(0, 0, 0, 8, 0, 16, 0, 24);
    var ribbon = new MedianRibbon(straight, Mouth(straight, false, 1f), Mouth(straight, true, 1f), 1f);
    var lattice = LaneLattice(1);
    lattice.Prepare(ribbon, 1f, 1.5f, 7.5f);
    var samples = new List<NodeSurface.Sample>();
    for (int i = 0; i <= 20; i++)
        for (int j = 0; j <= 48; j++)
        {
            float across = i / 20f, time = j / 48f;
            lattice.Locate(across, time, out int column, out int row, out float u, out float v);
            lattice.Triangle(column, row, u, v, lattice.Diagonal(column, row), out var corners, out var weights);
            foreach (float layer in new[] { 0f, 0.2f * time * (1f - time) })
                samples.Add(new NodeSurface.Sample
                {
                    Across = across, Time = time, Height = layer, Rise = layer, Corners = corners, Weights = weights
                });
        }
    Check(NodeSurface.Seat(samples, lattice, StripMargin, 0.3f, false, float2.zero, out var seating) &&
        seating.Floating < 0.03f,
        $"two layers of pavement: the strip rests {seating.Floating * 100f:F1} cm above the upper one");
}

// Pavement past the ribbon's mouths belongs to the roads beyond, not to the
// junction, and is not sampled.
static void CheckMouths()
{
    var straight = Curve(0, 0, 0, 8, 0, 16, 0, 24);
    var ribbon = new MedianRibbon(straight, Mouth(straight, false, 1f), Mouth(straight, true, 1f), 1f);
    ribbon.Locate(new float3(4f, 0f, 12f), out _, out float lateral);
    float side = math.sign(lateral);
    var lattice = LaneLattice(1);
    lattice.Prepare(ribbon, side, 1.5f, 7.5f);
    var offsets = new float4(0f, 0.375f, 0.625f, 1f);
    var samples = new List<NodeSurface.Sample>();
    NodeSurface.Rise(ribbon, side, 1.5f, 7.5f, lattice, Curve(2, 4, 2, 9, 2, 14, 2, 19), Curve(7, 4, 7, 9, 7, 14, 7, 19),
        offsets, offsets, 0f, 1f, 0, samples);
    int inside = samples.Count;
    samples.Clear();
    NodeSurface.Rise(ribbon, side, 1.5f, 7.5f, lattice, Curve(2, 26, 2, 30, 2, 34, 2, 38), Curve(7, 26, 7, 30, 7, 34, 7, 38),
        offsets, offsets, 0f, 1f, 0, samples);
    Check(inside > 0 && samples.Count == 0, "pavement past the ribbon's mouth is not sampled");
}

static Bezier4x3 C(params float[] v) => new Bezier4x3(new float3(v[0], v[1], v[2]), new float3(v[3], v[4], v[5]),
    new float3(v[6], v[7], v[8]), new float3(v[9], v[10], v[11]));

static float Evaluate(float4 controls, float t)
{
    float3 first = math.lerp(controls.xyz, controls.yzw, t);
    float2 second = math.lerp(first.xy, first.yz, t);
    return math.lerp(second.x, second.y, t);
}

static void CheckRenderRails(string name, MedianRibbon ribbon, float roadHalfWidth)
{
    const float lateralOffset = 1.25f;
    float width = 2f * roadHalfWidth;
    var origin = new float3(3f, 1f, -10f);
    var station = new float4(12f, 14f, 17f, 19f);
    var first = new MedianRenderRails(ribbon, false);
    var second = new MedianRenderRails(ribbon, true);
    first.Sample(1f, out var nearLeft, out var nearRight);
    second.Sample(1f, out var farLeft, out var farRight);
    Check(math.distance(nearLeft, farRight) < 0.0001f && math.distance(nearRight, farLeft) < 0.0001f,
        $"{name}: the independent rails meet at full width");
    // The game's node mesh maps the kept half of a shape-preserving piece of
    // half length L to saturate((L - z) / W) along the road. A tile shorter
    // than the road width starts at the mouth; a longer one is clamped to it.
    foreach (float tileHalfLength in new[] { 4f, 32f })
    {
        float halfMesh = math.min(tileHalfLength, width);
        first.Pack(width, lateralOffset, halfMesh, ribbon.HalfWidth, origin, station, out var a, out var b);
        for (int i = 0; i <= 8; i++)
        {
            float t = i / 8f;
            float z = tileHalfLength - t * halfMesh;
            float meshT = math.saturate((tileHalfLength - z) / width);
            var left = At(a, meshT);
            var right = At(b, meshT);
            float edgeX = 0.5f + (lateralOffset - ribbon.HalfWidth) / width;
            float stripX = 0.5f + (lateralOffset + ribbon.HalfWidth) / width;
            first.Sample(t, out var curbLeft, out var curbRight);
            Check(math.distance(math.lerp(left, right, edgeX).xyz + origin, curbLeft) < 0.001f &&
                math.distance(math.lerp(left, right, stripX).xyz + origin, curbRight) < 0.001f,
                $"{name}: packing preserves the native piece width and curve for a {tileHalfLength} m half tile");
            Check(math.abs(left.w - right.w) < 0.0001f && math.abs(left.w - Evaluate(station, t)) < 0.0001f,
                $"{name}: texture distance is constant across the native profile");
        }
    }

}

static float4 At(float4x4 curve, float t)
{
    float4 a = math.lerp(curve.c0, curve.c1, t);
    float4 b = math.lerp(curve.c1, curve.c2, t);
    float4 c = math.lerp(curve.c2, curve.c3, t);
    return math.lerp(math.lerp(a, b, t), math.lerp(b, c, t), t);
}

static MedianMouth Mouth(Bezier4x3 curve, bool end, float halfWidth)
{
    float3 point = end ? curve.d : curve.a;
    float3 tangent = end ? curve.d - curve.c : curve.b - curve.a;
    float2 normal = MathUtils.Left(math.normalize(tangent.xz)) * halfWidth;
    float3 offset = new float3(normal.x, 0, normal.y);
    return new MedianMouth(point + offset, point - offset, tangent);
}

static Bezier4x3 Outside(Bezier4x3 center, float width, float flare)
{
    var start = Mouth(center, false, width).Left - center.a;
    var end = Mouth(center, true, width + flare).Left - center.d;
    return new Bezier4x3(center.a + start, center.b + start, center.c + end, center.d + end);
}

static Bezier4x3 Shift(Bezier4x3 curve, float x)
{
    var shift = new float3(x, 0, 0);
    return new Bezier4x3(curve.a + shift, curve.b + shift, curve.c + shift, curve.d + shift);
}

static Bezier4x3 Curve(float ax, float az, float bx, float bz, float cx, float cz, float dx, float dz)
{
    return new Bezier4x3(new float3(ax, 0, az), new float3(bx, 0, bz), new float3(cx, 0, cz), new float3(dx, 0, dz));
}

static void Check(bool result, string label)
{
    if (!result) throw new InvalidOperationException(label);
}

// A logged closed junction with the pavement around its through-lane strip:
// the halves of the two through roads on the side street's side and the
// side street's four rails, as the fit hands them to the strip.
sealed class StripFixture
{
    public readonly string Name;
    public readonly Bezier4x3 Center;
    public readonly MedianMouth Start, End;
    public readonly Bezier4x3[] First, Second, Street;
    public readonly float4 FirstOffsets, FirstTargets, SecondOffsets, SecondTargets, StreetLeft, StreetRight;
    public readonly bool FirstIsLeft, SecondIsLeft;
    // Regression values for the settled middle rail.
    public float2 ExpectedSettle;

    // The logged form: the first through road's middle and outer right rails,
    // the second's outer and middle left rails, and the side street's rails
    // from left to right.
    public StripFixture(string name, Bezier4x3 center, MedianMouth start, MedianMouth end, Bezier4x3[] first,
        float4 firstTargets, Bezier4x3[] second, float4 secondTargets, float4 syncLeft, float4 syncRight,
        Bezier4x3[] street, float4 streetLeft, float4 streetRight)
        : this(name, center, start, end, first, syncRight, firstTargets, false, second, syncLeft, secondTargets, true,
            street, streetLeft, streetRight) { }

    private StripFixture(string name, Bezier4x3 center, MedianMouth start, MedianMouth end,
        Bezier4x3[] first, float4 firstOffsets, float4 firstTargets, bool firstIsLeft,
        Bezier4x3[] second, float4 secondOffsets, float4 secondTargets, bool secondIsLeft,
        Bezier4x3[] street, float4 streetLeft, float4 streetRight)
    {
        Name = name;
        Center = center;
        Start = start;
        End = end;
        First = first;
        FirstOffsets = firstOffsets;
        FirstTargets = firstTargets;
        FirstIsLeft = firstIsLeft;
        Second = second;
        SecondOffsets = secondOffsets;
        SecondTargets = secondTargets;
        SecondIsLeft = secondIsLeft;
        Street = street;
        StreetLeft = streetLeft;
        StreetRight = streetRight;
    }

    public MedianRibbon Ribbon => new MedianRibbon(Center, Start, End, 2.10004f);

    // The raise of the middle rail's inner control points to an even grade.
    public float2 Grade
    {
        get
        {
            var middle = Street[1];
            float3 straight = (middle.d - middle.a) / 3f;
            return new float2(middle.a.y + straight.y, middle.d.y - straight.y) - new float2(middle.b.y, middle.c.y);
        }
    }

    private StripFixture With(string name, Bezier4x3 center, MedianMouth start, MedianMouth end, Bezier4x3[] first,
        Bezier4x3[] second, Bezier4x3[] street) =>
        new StripFixture(name, center, start, end, first, FirstOffsets, FirstTargets, FirstIsLeft, second, SecondOffsets,
            SecondTargets, SecondIsLeft, street, StreetLeft, StreetRight) { ExpectedSettle = ExpectedSettle };

    // The junction with the side street's middle rail moved as the strip settled it.
    public StripFixture Settled(float2 settle)
    {
        var street = (Bezier4x3[])Street.Clone();
        street[1].b.y += settle.x;
        street[1].c.y += settle.y;
        street[2].b.y += settle.x;
        street[2].c.y += settle.y;
        return With(Name, Center, Start, End, First, Second, street);
    }

    // The junction reflected across a north-south line: left and right trade
    // places, so do the halves of each road and the sync shares.
    public StripFixture Mirror()
    {
        static float3 R(float3 p) => new float3(-p.x, p.y, p.z);
        static Bezier4x3 B(Bezier4x3 c) => new Bezier4x3(R(c.a), R(c.b), R(c.c), R(c.d));
        static float4 Flip(float4 v) => 1f - v.wzyx;
        static MedianMouth M(MedianMouth m) => new MedianMouth(R(m.Right), R(m.Left), R(m.Tangent));
        return new StripFixture(Name, B(Center), M(Start), M(End),
            new[] { B(First[1]), B(First[0]) }, Flip(FirstOffsets), Flip(FirstTargets), !FirstIsLeft,
            new[] { B(Second[1]), B(Second[0]) }, Flip(SecondOffsets), Flip(SecondTargets), !SecondIsLeft,
            new[] { B(Street[3]), B(Street[2]), B(Street[1]), B(Street[0]) }, Flip(StreetRight), Flip(StreetLeft))
        { ExpectedSettle = ExpectedSettle };
    }

    // The junction bent over a crest along the through road, falling to the
    // mouths by the grade given there, or into a sag for a negative grade.
    public StripFixture Crest(float grade)
    {
        float3 mid = (Center.a + Center.d) * 0.5f;
        float2 direction = math.normalize((Center.d - Center.a).xz);
        float half = math.length((Center.d - Center.a).xz) * 0.5f;
        float k = grade / (2f * half);
        float S(float3 p) => math.dot(p.xz - mid.xz, direction);
        float3 P(float3 p) => p - new float3(0f, k * S(p) * S(p), 0f);
        float3 T(float3 p, float3 t) => t - new float3(0f, 2f * k * S(p) * math.dot(direction, t.xz), 0f);
        Bezier4x3 B(Bezier4x3 c) => new Bezier4x3(P(c.a), P(c.b), P(c.c), P(c.d));
        MedianMouth M(MedianMouth m) => new MedianMouth(P(m.Left), P(m.Right), T(m.Center, m.Tangent));
        string name = grade > 0f ? $"{Name} on a crest of {grade * 100f:F0}%" : $"{Name} in a sag of {-grade * 100f:F0}%";
        return With(name, B(Center), M(Start), M(End), Array.ConvertAll(First, B), Array.ConvertAll(Second, B),
            Array.ConvertAll(Street, B));
    }
}
