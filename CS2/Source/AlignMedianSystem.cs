using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;

namespace ConditionalMedians2
{
    public sealed partial class AlignMedianSystem : GameSystemBase
    {
        private EntityQuery closures;
        private EntityQuery roadUpdates;
        private MedianSystem medians;
        private MedianRenderMesh renderMesh;
        private readonly List<TravelLane> lanes = new List<TravelLane>(12);
        private readonly List<MedianSection> sections = new List<MedianSection>(64);
        private readonly List<MedianSection> fittedSections = new List<MedianSection>(64);
        private readonly Dictionary<Entity, float> curbWidths = new Dictionary<Entity, float>();
        private readonly Dictionary<Entity, float> tilePeriods = new Dictionary<Entity, float>();
        private readonly Dictionary<Entity, uint> reportedMarkers = new Dictionary<Entity, uint>();
        private readonly HashSet<Entity> aligned = new HashSet<Entity>();
        private readonly List<MedianRenderMesh.StripPiece> stripLanes = new List<MedianRenderMesh.StripPiece>(4);
        private readonly List<MedianRenderMesh.StripPiece> otherLanes = new List<MedianRenderMesh.StripPiece>(4);
        private readonly List<MedianRenderMesh.StripPiece> laneScratch = new List<MedianRenderMesh.StripPiece>(8);
        private readonly List<NodeSurface.Sample> stripSamples = new List<NodeSurface.Sample>(2048);
        private readonly NodeSurface.Lattice stripLattice = new NodeSurface.Lattice();
        private readonly Dictionary<Entity, uint> reportedStrips = new Dictionary<Entity, uint>();
        // Side-street ends refused a strip because another junction draws the
        // strip mesh of their composition for other lanes or another skirt.
        private readonly Dictionary<(Entity Edge, bool Starts), BlockedStrip> blockedStrips =
            new Dictionary<(Entity, bool), BlockedStrip>();
        private readonly List<(Entity Edge, bool Starts)> unblocked = new List<(Entity, bool)>();
        private readonly Dictionary<Entity, uint> diagnosedStreets = new Dictionary<Entity, uint>();
        // Road ends at closed junctions that another mod draws with its own copy
        // of the junction mesh. The copy has no shape rails, so a strip or a
        // divider on its own rails would follow the pavement instead.
        private readonly HashSet<(Entity Edge, bool Starts)> foreignStreets = new HashSet<(Entity, bool)>();
        private readonly HashSet<Entity> foreignDividers = new HashSet<Entity>();
        // The native set as worked out now, at the previous pass, and the
        // compositions that entered or left it in between.
        private readonly HashSet<Entity> nativeSet = new HashSet<Entity>();
        private readonly HashSet<Entity> nativeSeen = new HashSet<Entity>();
        private readonly HashSet<Entity> nativeChanged = new HashSet<Entity>();
        private readonly HashSet<Entity> nativeBefore = new HashSet<Entity>();
        private readonly List<(Entity Node, Entity First, Entity Second)> nativeUpdates =
            new List<(Entity, Entity, Entity)>();
        private readonly Dictionary<Entity, uint> diagnosedGeometry = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> diagnosedTexture = new Dictionary<Entity, uint>();
        private readonly HashSet<Entity> diagnosedMeshes = new HashSet<Entity>();
        private readonly HashSet<Entity> diagnosedPieces = new HashSet<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
            renderMesh = new MedianRenderMesh(EntityManager);
            closures = GetEntityQuery(ComponentType.ReadOnly<ClosedMedian>(), ComponentType.ReadOnly<SubLane>(),
                ComponentType.Exclude<Deleted>());
            roadUpdates = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Composition>() },
                Any = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });
            RequireForUpdate(closures);
            RequireForUpdate(roadUpdates);
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            curbWidths.Clear();
            tilePeriods.Clear();
            reportedMarkers.Clear();
            reportedStrips.Clear();
            blockedStrips.Clear();
            foreignStreets.Clear();
            foreignDividers.Clear();
            nativeSeen.Clear();
            diagnosedStreets.Clear();
            aligned.Clear();
            diagnosedGeometry.Clear();
            diagnosedTexture.Clear();
            diagnosedMeshes.Clear();
            diagnosedPieces.Clear();
            renderMesh.ResetPending();
        }

        // Whether this pass has read a closure's two roads since the city was loaded.
        internal bool HasAligned(Entity node) => aligned.Contains(node);

        internal void KnownMeshes(HashSet<Entity> known) => renderMesh.CollectKnown(known);

        // Called by the renderer once another mod has drawn a closed junction's
        // road end with its own mesh for a while. The junction is fitted again
        // without the shape rails there.
        internal void ForeignMesh(Entity node, Entity edge, bool starts, bool divider)
        {
            if (divider ? !foreignDividers.Add(node) : !foreignStreets.Add((edge, starts))) return;
            Mod.Log.Info(divider
                ? $"Another mod draws road {edge.Index} at closed junction {node.Index} with its own junction mesh. " +
                  "The divider there is drawn with the junction's native deformation."
                : $"Another mod draws side street {edge.Index} at closed junction {node.Index} with its own junction mesh. " +
                  "That side street draws the game's own mesh and no through-lane strip.");
            medians.RefreshJunction(node);
        }

        // Nothing marks a junction updated when another junction stops drawing
        // the strip mesh it was refused for: that junction may lose its strip,
        // be removed, or have its median rejected. Runs every frame, also when
        // no road changed, and asks for one more fit of each junction whose
        // side street is no longer blocked.
        internal void RetryBlockedStrips()
        {
            if (blockedStrips.Count == 0) return;
            unblocked.Clear();
            foreach (var item in blockedStrips)
                if (!EntityManager.HasComponent<ClosedMedian>(item.Value.Node) ||
                    !renderMesh.Blocks(item.Value.Composition, item.Value.Signature, item.Key.Edge, item.Key.Starts))
                    unblocked.Add(item.Key);
            foreach (var key in unblocked)
            {
                Entity node = blockedStrips[key].Node;
                blockedStrips.Remove(key);
                if (EntityManager.HasComponent<ClosedMedian>(node)) medians.RefreshJunction(node);
            }
        }

        internal void PrepareRenderMeshes() => renderMesh.Prepare();
        internal void CollectRenderMeshes() => renderMesh.Collect();
        internal Dictionary<Entity, Entity>.KeyCollection StripMeshes => renderMesh.StripMeshes;

        protected override void OnUpdate()
        {
            using (var nodes = closures.ToEntityArray(Allocator.Temp))
            foreach (Entity node in nodes)
            {
                var median = EntityManager.GetComponentData<ClosedMedian>(node);
                // The game recomputes the junction geometry of each updated road,
                // a side street included, and that drops this fit's join on it.
                // Fitting again from roads the game left alone returns the same rails.
                if (!EntityManager.HasComponent<Updated>(node) &&
                    !EntityManager.HasComponent<Updated>(median.FirstRoad) &&
                    !EntityManager.HasComponent<Updated>(median.SecondRoad) &&
                    !AnyRoadUpdated(node)) continue;
                if (!ReadRoad(node, median.FirstRoad, false, out var first) ||
                    !ReadRoad(node, median.SecondRoad, true, out var second)) continue;

                if (!first.MeshReady || !second.MeshReady)
                {
                    medians.WaitForMedianMesh(node, first.Piece, second.Piece);
                    continue;
                }
                aligned.Add(node);
                ReadLanes(node, median, first.Mouth, second.Mouth, out var guide);
                Hash128 inputs = new Hash128(
                    Fingerprint(first.Mouth.Left) ^ Fingerprint(second.Mouth.Left),
                    Fingerprint(first.Mouth.Right) ^ Fingerprint(second.Mouth.Right),
                    Fingerprint(math.normalizesafe(first.Mouth.Tangent)) ^ (31u * Fingerprint(math.normalizesafe(second.Mouth.Tangent))),
                    math.hash(new float2(first.HalfWidth, second.HalfWidth)));
                foreach (var connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                {
                    if (!EntityManager.TryGetComponent<Curve>(connection.m_Edge, out var road)) continue;
                    var curve = road.m_Bezier;
                    inputs.Value.z ^= Fingerprint(curve.a) + 31u * Fingerprint(curve.b) +
                        397u * Fingerprint(curve.c) + 997u * Fingerprint(curve.d);
                }
                foreach (var lane in lanes)
                    inputs.Value.w ^= Fingerprint(lane.Curve.a) + 31u * Fingerprint(lane.Curve.b) +
                        397u * Fingerprint(lane.Curve.c) + 997u * Fingerprint(lane.Curve.d) + math.asuint(lane.HalfWidth);
                if (medians.KeepsRejection(node, inputs)) continue;
                if (!TryAlign(node, first, second, guide, out string reason))
                {
                    MedianTextureSystem.Clear(EntityManager, first.Edge, node);
                    MedianTextureSystem.Clear(EntityManager, second.Edge, node);
                    HideStrips(node, first.Edge, second.Edge);
                    medians.ReportGeometry(node, inputs, reason);
                }
                else
                    medians.ReportGeometry(node, inputs, null);
            }
        }

        private bool AnyRoadUpdated(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            foreach (var connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (EntityManager.HasComponent<Updated>(connection.m_Edge)) return true;
            return false;
        }

        // A node composition is shared by every junction that selects it, and a
        // junction draws both halves of its divider the same way. The native set
        // is therefore every composition of a junction that needs the native
        // deformation, grown over closed junctions that share a composition
        // with it. Runs every frame, also when no road changed: a request ends
        // when its junction is fitted on its own rails again, reopened, rejected
        // or removed. The junctions that use a composition entering or leaving
        // the set are fitted again, so their dividers, pavement tucks and strips
        // follow.
        internal void RetryNativeFallbacks()
        {
            if (!renderMesh.HasNative && nativeSeen.Count == 0) return;
            // A request follows its junction's current compositions, which can
            // change on fits that end before the native decision, and ends with
            // the junction's closure or its median fit.
            nativeUpdates.Clear();
            foreach (var request in renderMesh.NativeRequests)
            {
                Entity node = request.Key;
                if (IsStaleNative(node))
                {
                    nativeUpdates.Add((node, Entity.Null, Entity.Null));
                    continue;
                }
                var median = EntityManager.GetComponentData<ClosedMedian>(node);
                Entity first = NodeComposition(median.FirstRoad, node);
                Entity second = NodeComposition(median.SecondRoad, node);
                if (first != request.Value.First || second != request.Value.Second)
                    nativeUpdates.Add((node, first, second));
            }
            foreach (var update in nativeUpdates)
            {
                if (update.First == Entity.Null || update.Second == Entity.Null) renderMesh.ReleaseNative(update.Node);
                else renderMesh.RequestNative(update.Node, update.First, update.Second);
            }
            // Compositions switched during the last fits count as changed too:
            // their junctions are fitted again if the set no longer holds them.
            nativeBefore.Clear();
            renderMesh.CollectNative(nativeBefore);
            nativeBefore.UnionWith(nativeSeen);
            nativeSet.Clear();
            renderMesh.CollectRequested(nativeSet);
            using (var nodes = closures.ToEntityArray(Allocator.Temp))
            {
                bool grown = nativeSet.Count != 0;
                while (grown)
                {
                    grown = false;
                    foreach (var node in nodes)
                    {
                        if (!medians.CanRenderMedian(node)) continue;
                        var median = EntityManager.GetComponentData<ClosedMedian>(node);
                        Entity first = NodeComposition(median.FirstRoad, node);
                        Entity second = NodeComposition(median.SecondRoad, node);
                        if (first == Entity.Null || second == Entity.Null ||
                            nativeSet.Contains(first) == nativeSet.Contains(second)) continue;
                        nativeSet.Add(first);
                        nativeSet.Add(second);
                        grown = true;
                    }
                }
                renderMesh.SetNative(nativeSet);
                nativeChanged.Clear();
                foreach (var composition in nativeBefore) if (!nativeSet.Contains(composition)) nativeChanged.Add(composition);
                foreach (var composition in nativeSet) if (!nativeSeen.Contains(composition)) nativeChanged.Add(composition);
                if (nativeChanged.Count == 0) return;
                nativeSeen.Clear();
                nativeSeen.UnionWith(nativeSet);
                foreach (var node in nodes)
                {
                    var median = EntityManager.GetComponentData<ClosedMedian>(node);
                    if (nativeChanged.Contains(NodeComposition(median.FirstRoad, node)) ||
                        nativeChanged.Contains(NodeComposition(median.SecondRoad, node)))
                        medians.RefreshJunction(node);
                }
            }
        }

        private bool IsStaleNative(Entity node) =>
            !EntityManager.Exists(node) || !EntityManager.HasComponent<ClosedMedian>(node) ||
            EntityManager.HasComponent<Deleted>(node) || !medians.CanRenderMedian(node);

        private Entity NodeComposition(Entity edge, Entity node)
        {
            if (!EntityManager.TryGetComponent<Game.Net.Edge>(edge, out var ends) ||
                !EntityManager.TryGetComponent<Composition>(edge, out var selected)) return Entity.Null;
            return ends.m_Start == node ? selected.m_StartNode : ends.m_End == node ? selected.m_EndNode : Entity.Null;
        }

        private static uint Fingerprint(float3 point) => math.hash((int3)math.round(point * 20f));

        private bool TryAlign(Entity node, Approach first, Approach second, Bezier4x3 guide, out string reason)
        {
            float width = first.HalfWidth;
            if (math.abs(width - second.HalfWidth) > MedianRibbon.JoinTolerance * 0.5f)
            {
                reason = "the approaches have different native curb widths";
                return false;
            }
            var center = MedianRibbon.FitCenter(first.Mouth, second.Mouth, guide);
            var ribbon = new MedianRibbon(center, first.Mouth, second.Mouth, width);
            if (!ribbon.Sample(first.Mouth, second.Mouth, sections, out reason)) return false;
            foreach (var lane in lanes)
            {
                float clearance = MedianRibbon.MinimumClearance(sections, lane.Curve);
                if (clearance >= math.max(0f, lane.HalfWidth - MedianRibbon.JoinTolerance)) continue;
                reason = $"the native-width median would enter a travel lane ({clearance:F3} m from center, " +
                    $"{lane.HalfWidth:F3} m lane half width, {2f * ribbon.HalfWidth:F3} m native median)";
                return false;
            }

            // An earlier failed fit can leave an open visual composition until
            // composition selection runs again. Validate the ribbon to allow retry.
            if (!first.Closed || !second.Closed) return true;
            var a = first.Geometry;
            var b = second.Geometry;
            float2 aOffsets = Fractions(first);
            float2 bOffsets = Fractions(second);
            var firstRails = new NativeCurbFit.RailPair(ribbon, a.m_Left.m_Right, a.m_Left.m_Left, aOffsets.x,
                a.m_Right.m_Left, a.m_Right.m_Right, aOffsets.y, false);
            var secondRails = new NativeCurbFit.RailPair(ribbon, b.m_Right.m_Left, b.m_Right.m_Right, bOffsets.y,
                b.m_Left.m_Right, b.m_Left.m_Left, bOffsets.x, true);
            NativeCurbFit.Fit(ribbon, firstRails, secondRails, out var aLeft, out var aRight, out var bLeft, out var bRight);
            var startDirection = sections[0].Direction;
            var endDirection = sections[sections.Count - 1].Direction;
            if (!aLeft.Joins(bLeft, first.Mouth.Left, second.Mouth.Left, startDirection, endDirection) ||
                !aRight.Joins(bRight, first.Mouth.Right, second.Mouth.Right, startDirection, endDirection))
            {
                reason = "the fitted curb does not meet the native join or its tangent";
                return false;
            }
            if (!CheckNativeFit(ribbon, aLeft, aRight, bLeft, bRight, out reason)) return false;

            // The sync marker sits at the outer edge of the divider piece, where
            // the lane pavement begins. A marker at the curb face would leave that
            // pavement edge to the next lane marker, and it would drift away from
            // the divider's skirt toward the center of the junction.
            float aScale = MarkerScaleFor(first, aLeft, aRight);
            float bScale = MarkerScaleFor(second, bRight, bLeft);

            // A divider on its own rails leaves no pavement under its skirt, and
            // its straight segments start at other stations than the pavement's.
            // Tuck the pavement under the skirt there. A natively deformed
            // divider shares its edge with the pavement and keeps the fit as is.
            bool dedicated = MapTexture(node, first, second, ribbon, a, b,
                new float4(aOffsets, new float2(aLeft.SeamFraction, aRight.SeamFraction)),
                new float4(bOffsets, new float2(bRight.SeamFraction, bLeft.SeamFraction)), out float2 stations);
            float tuck = dedicated
                ? NativeCurbFit.SkirtTuck(ribbon, sections, aLeft, aRight, bLeft, bRight, aScale, bScale) : 0f;
            var aLeftRail = aLeft.Tucked(ribbon, false, true, tuck);
            var aRightRail = aRight.Tucked(ribbon, false, false, tuck);
            var bLeftRail = bLeft.Tucked(ribbon, true, true, tuck);
            var bRightRail = bRight.Tucked(ribbon, true, false, tuck);

            SetCurbMarkers(first.NodeComposition, first.NodeData, aOffsets * aScale, out var aSyncLeft, out var aSyncRight);
            SetCurbMarkers(second.NodeComposition, second.NodeData, bOffsets * bScale, out var bSyncLeft, out var bSyncRight);
            var aStreet = a;
            var bStreet = b;
            ApplyFits(ref a, ref aStreet, aLeftRail, aRightRail, aScale, aSyncLeft, aSyncRight);
            ApplyFits(ref b, ref bStreet, bRightRail, bLeftRail, bScale, bSyncLeft, bSyncRight);
            MathUtils.Divide(center, out a.m_Middle, out var secondHalf, 0.5f);
            b.m_Middle = MathUtils.Invert(secondHalf);
            if (MedianDiagnostics.Enabled)
                DiagnoseGeometry(node, first, second, ribbon, a, b, aScale, bScale,
                    new float4(aSyncLeft.w, aSyncRight.x, bSyncLeft.w, bSyncRight.x),
                    aSyncLeft, aSyncRight, bSyncLeft, bSyncRight);
            // A side street's pavement meets each half of this road along the line
            // from the shared corner to that half's inner rail. End the side
            // street on the rails as written, so both edges stay on one line.
            var strip = new StripContext
            {
                Node = node, Dedicated = dedicated, Ribbon = ribbon, First = first, Second = second, A = a, B = b,
                ASyncLeft = aSyncLeft, ASyncRight = aSyncRight, BSyncLeft = bSyncLeft, BSyncRight = bSyncRight,
                Stations = stations
            };
            JoinSideStreets(node, first.Edge, second.Edge, aStreet, bStreet,
                aLeftRail.Curb(1f), aRightRail.Curb(1f), bRightRail.Curb(1f), bLeftRail.Curb(1f), strip);
            WriteApproach(first.Edge, first.Starts, a);
            WriteApproach(second.Edge, second.Starts, b);
            ReportMarkers(node, NativeCurbFit.SkirtRange(ribbon, sections, aLeftRail, aRightRail, bLeftRail, bRightRail,
                    aScale, bScale), tuck,
                new float4(aOffsets, bOffsets), new float4(1f - aSyncLeft.w, aSyncRight.x, 1f - bSyncLeft.w, bSyncRight.x),
                new float4(1f - a.m_SyncVertexTargetsLeft.w, a.m_SyncVertexTargetsRight.x,
                    1f - b.m_SyncVertexTargetsLeft.w, b.m_SyncVertexTargetsRight.x));
            return true;
        }

        // Returns whether both halves of the divider are drawn on their own rails.
        private bool MapTexture(Entity node, Approach first, Approach second, MedianRibbon ribbon,
            EdgeNodeGeometry a, EdgeNodeGeometry b,
            float4 firstOffsets, float4 secondOffsets, out float2 stations)
        {
            bool dedicated = false;
            stations = 0f;
            if (!tilePeriods.TryGetValue(first.Piece, out float period) || period <= 0f || first.Piece != second.Piece)
            {
                // No mapping is written, so the junction draws natively; one left
                // from an earlier fit would keep a half on its dedicated rails.
                // Halves with two different dividers keep their compositions
                // native, so no other junction gives them a dedicated mesh
                // without rails here. A new composition is decided on once it
                // has its mesh.
                MedianTextureSystem.Clear(EntityManager, first.Edge, node);
                MedianTextureSystem.Clear(EntityManager, second.Edge, node);
                if (!renderMesh.Ready(first.NodeComposition) || !renderMesh.Ready(second.NodeComposition))
                    medians.WaitForMedianRenderMesh(node, first.NodeComposition, second.NodeComposition);
                else
                {
                    renderMesh.ReleaseNative(node);
                    if (first.Piece != second.Piece)
                        renderMesh.RequestNative(node, first.NodeComposition, second.NodeComposition);
                }
                return false;
            }
            MedianTexturePhase.Map(ribbon.Center, first.MouthStation, second.MouthStation, period, !first.Starts,
                first.Starts != second.Starts, out var near, out var far);
            // The texture distance at the first and at the second mouth.
            stations = new float2(near.x, far.x);
            var nearMapping = new MedianTextureHalf
            {
                Junction = node, Composition = first.NodeComposition, Station = near, Fractions = firstOffsets
            };
            var farMapping = new MedianTextureHalf
            {
                Junction = node, Composition = second.NodeComposition, Station = far, Fractions = secondOffsets
            };
            var nearRails = new MedianRenderRails(ribbon, false);
            var farRails = new MedianRenderRails(ribbon, true);
            if (!renderMesh.Ready(first.NodeComposition) || !renderMesh.Ready(second.NodeComposition))
            {
                // Keeps what this junction asked for until it can decide again.
                medians.WaitForMedianRenderMesh(node, first.NodeComposition, second.NodeComposition);
            }
            else
            {
                // This junction's own request is taken back; it asks again if its
                // divider still cannot be drawn on its own rails.
                renderMesh.ReleaseNative(node);
                if (MedianDiagnostics.NativeJunction == node.Index || foreignDividers.Contains(node) ||
                    !renderMesh.Describe(first.NodeComposition, first.Piece, out var nearLayout) ||
                    !renderMesh.Describe(second.NodeComposition, second.Piece, out var farLayout) ||
                    !FitsRenderRails(ribbon, nearRails, farRails))
                {
                    renderMesh.RequestNative(node, first.NodeComposition, second.NodeComposition);
                }
                else if (renderMesh.IsNativeOnly(first.NodeComposition) || renderMesh.IsNativeOnly(second.NodeComposition))
                {
                    // Another closed junction that shares a composition with this one
                    // needs the native deformation. Both halves are drawn natively.
                    renderMesh.FollowNative(first.NodeComposition, second.NodeComposition);
                }
                else
                {
                    bool nearReady = renderMesh.TryGetMesh(nearLayout, out var nearMesh);
                    bool farReady = renderMesh.TryGetMesh(farLayout, out var farMesh);
                    if (nearReady && farReady)
                    {
                        renderMesh.Assign(first.NodeComposition, nearMesh);
                        renderMesh.Assign(second.NodeComposition, farMesh);
                        nearMapping.DedicatedMesh = nearMesh;
                        farMapping.DedicatedMesh = farMesh;
                        dedicated = true;
                        nearRails.Pack(nearLayout.Width, nearLayout.Offset, nearLayout.HalfLength,
                            ribbon.HalfWidth, MathUtils.Center(a.m_Bounds), near, out nearMapping.LeftRail, out nearMapping.RightRail);
                        farRails.Pack(farLayout.Width, farLayout.Offset, farLayout.HalfLength,
                            ribbon.HalfWidth, MathUtils.Center(b.m_Bounds), far, out farMapping.LeftRail, out farMapping.RightRail);
                    }
                    else medians.WaitForMedianRenderMesh(node, first.NodeComposition, second.NodeComposition);
                }
            }
            SetTexture(first, nearMapping);
            SetTexture(second, farMapping);
            if (MedianDiagnostics.Enabled) DiagnoseTexture(node, first, second, ribbon, period, nearMapping, farMapping);
            return dedicated;
        }

        private void DiagnoseGeometry(Entity node, Approach first, Approach second, MedianRibbon ribbon,
            EdgeNodeGeometry a, EdgeNodeGeometry b, float aScale, float bScale, float4 markers,
            float4 aSyncLeft, float4 aSyncRight, float4 bSyncLeft, float4 bSyncRight)
        {
            uint state = math.hash(new float4x4(new float4(ribbon.Center.a, a.m_SyncVertexTargetsLeft.w),
                new float4(ribbon.Center.b, a.m_SyncVertexTargetsRight.x),
                new float4(ribbon.Center.c, b.m_SyncVertexTargetsLeft.w),
                new float4(ribbon.Center.d, b.m_SyncVertexTargetsRight.x))) ^ math.hash(markers) ^
                math.hash(new float3x4(a.m_Left.m_Right.c, a.m_Right.m_Left.c, b.m_Left.m_Right.c, b.m_Right.m_Left.c));
            if (diagnosedGeometry.TryGetValue(node, out uint reported) && reported == state) return;
            diagnosedGeometry[node] = state;
            Mod.Log.Info($"DIAG geometry junction {node.Index}: center {MedianDiagnostics.B(ribbon.Center)} " +
                $"halfWidth {MedianDiagnostics.N(ribbon.HalfWidth)}");
            DiagnoseApproach("first", node, first, a, aScale, aSyncLeft, aSyncRight);
            DiagnoseApproach("second", node, second, b, bScale, bSyncLeft, bSyncRight);
        }

        private void DiagnoseApproach(string name, Entity node, Approach approach, EdgeNodeGeometry fitted,
            float scale, float4 syncLeft, float4 syncRight)
        {
            var data = approach.NodeData;
            Mod.Log.Info($"DIAG {name} junction {node.Index}: edge {approach.Edge.Index} starts {approach.Starts} " +
                $"mouthStation {MedianDiagnostics.N(approach.MouthStation)} halfWidth {MedianDiagnostics.N(approach.HalfWidth)} " +
                $"markerScale {MedianDiagnostics.N(scale)} piece {approach.Piece.Index} composition {approach.NodeComposition.Index} " +
                $"width {MedianDiagnostics.N(data.m_Width)} middleOffset {MedianDiagnostics.N(data.m_MiddleOffset)} " +
                $"readOffsetsL{MedianDiagnostics.V(data.m_SyncVertexOffsetsLeft)} readOffsetsR{MedianDiagnostics.V(data.m_SyncVertexOffsetsRight)} " +
                $"offsetsL{MedianDiagnostics.V(syncLeft)} offsetsR{MedianDiagnostics.V(syncRight)} " +
                $"mouthL{MedianDiagnostics.V(approach.Mouth.Left)} mouthR{MedianDiagnostics.V(approach.Mouth.Right)} " +
                $"mouthT{MedianDiagnostics.V(approach.Mouth.Tangent)}");
            Mod.Log.Info($"DIAG {name} native junction {node.Index}: {MedianDiagnostics.G(approach.Geometry)}");
            Mod.Log.Info($"DIAG {name} fitted junction {node.Index}: {MedianDiagnostics.G(fitted)}");
            DiagnoseMesh(approach.NodeComposition);
        }

        private void DiagnoseTexture(Entity node, Approach first, Approach second, MedianRibbon ribbon, float period,
            MedianTextureHalf near, MedianTextureHalf far)
        {
            uint state = math.hash(new float4x4(near.Station, far.Station,
                new float4(near.DedicatedMesh.Index, far.DedicatedMesh.Index, period, 0f), near.LeftRail.c0));
            if (diagnosedTexture.TryGetValue(node, out uint reported) && reported == state) return;
            diagnosedTexture[node] = state;
            MathUtils.Divide(ribbon.Center, out var nearHalf, out var farHalf, 0.5f);
            Mod.Log.Info($"DIAG texture junction {node.Index}: period {MedianDiagnostics.N(period)} " +
                $"nearLength {MedianDiagnostics.N(MathUtils.Length(nearHalf))} farLength {MedianDiagnostics.N(MathUtils.Length(farHalf))} " +
                $"firstStarts {first.Starts} secondStarts {second.Starts} " +
                $"firstMouth {MedianDiagnostics.N(first.MouthStation)} secondMouth {MedianDiagnostics.N(second.MouthStation)} " +
                $"near{MedianDiagnostics.V(near.Station)} far{MedianDiagnostics.V(far.Station)} " +
                $"nearFractions{MedianDiagnostics.V(near.Fractions)} farFractions{MedianDiagnostics.V(far.Fractions)} " +
                $"nearMesh {near.DedicatedMesh.Index} farMesh {far.DedicatedMesh.Index}");
            if (near.DedicatedMesh != Entity.Null)
                Mod.Log.Info($"DIAG rails junction {node.Index}: nearL{MedianDiagnostics.M(near.LeftRail)} " +
                    $"nearR{MedianDiagnostics.M(near.RightRail)} farL{MedianDiagnostics.M(far.LeftRail)} " +
                    $"farR{MedianDiagnostics.M(far.RightRail)}");
            DiagnoseMesh(first.NodeComposition);
            DiagnoseMesh(second.NodeComposition);
        }

        private void DiagnoseMesh(Entity composition)
        {
            if (!EntityManager.TryGetComponent<NetCompositionMeshRef>(composition, out var reference) ||
                reference.m_Mesh == Entity.Null || !diagnosedMeshes.Add(reference.m_Mesh) ||
                !EntityManager.HasBuffer<NetCompositionPiece>(reference.m_Mesh) ||
                !EntityManager.TryGetComponent<NetCompositionMeshData>(reference.m_Mesh, out var data)) return;
            Mod.Log.Info($"DIAG mesh {reference.m_Mesh.Index} of composition {composition.Index}: " +
                $"width {MedianDiagnostics.N(data.m_Width)} middleOffset {MedianDiagnostics.N(data.m_MiddleOffset)} " +
                $"flags {data.m_Flags.m_General} rotate {reference.m_Rotate}");
            var pieces = EntityManager.GetBuffer<NetCompositionPiece>(reference.m_Mesh, true).ToNativeArray(Allocator.Temp);
            for (int i = 0; i < pieces.Length; i++)
            {
                var piece = pieces[i];
                Mod.Log.Info($"DIAG mesh {reference.m_Mesh.Index} piece {i}: entity {piece.m_Piece.Index} " +
                    $"section {piece.m_SectionFlags} flags {piece.m_PieceFlags} size{MedianDiagnostics.V(piece.m_Size)} " +
                    $"offset{MedianDiagnostics.V(piece.m_Offset)}");
                DiagnosePiece(piece.m_Piece);
            }
            pieces.Dispose();
        }

        private void DiagnosePiece(Entity piece)
        {
            if (!diagnosedPieces.Add(piece) || !EntityManager.HasBuffer<MeshVertex>(piece)) return;
            var vertices = EntityManager.GetBuffer<MeshVertex>(piece, true);
            bool hasUv = EntityManager.HasBuffer<MeshUV0>(piece);
            var uv = hasUv ? EntityManager.GetBuffer<MeshUV0>(piece, true) : default;
            float3 low = float.PositiveInfinity;
            float3 high = float.NegativeInfinity;
            var stations = new List<int>();
            var surface = new List<int>();
            var mapping = new List<long>();
            for (int i = 0; i < vertices.Length; i++)
            {
                float3 p = vertices[i].m_Vertex;
                low = math.min(low, p);
                high = math.max(high, p);
                int z = (int)math.round(p.z * 1000f);
                if (!stations.Contains(z)) stations.Add(z);
                // Ground-level vertices carry the surface that meets the pavement.
                if (p.y < 0.02f)
                {
                    int x = (int)math.round(p.x * 1000f);
                    if (!surface.Contains(x)) surface.Add(x);
                }
                if (hasUv && uv[i].m_Uv.y >= 0f)
                {
                    long pair = ((long)z << 32) ^ (uint)(int)math.round(uv[i].m_Uv.y * 1000f);
                    if (!mapping.Contains(pair)) mapping.Add(pair);
                }
            }
            stations.Sort();
            surface.Sort();
            mapping.Sort();
            var text = new System.Text.StringBuilder();
            text.Append($"DIAG piece {piece.Index}: vertices {vertices.Length} low{MedianDiagnostics.V(low)} high{MedianDiagnostics.V(high)} z[mm]");
            for (int i = 0; i < stations.Count && i < 80; i++) text.Append(i == 0 ? " " : ",").Append(stations[i]);
            if (stations.Count > 80) text.Append($" (+{stations.Count - 80} more)");
            text.Append(" groundX[mm]");
            for (int i = 0; i < surface.Count && i < 40; i++) text.Append(i == 0 ? " " : ",").Append(surface[i]);
            text.Append(" zToV[mm:milli]");
            for (int i = 0; i < mapping.Count && i < 60; i++)
                text.Append(i == 0 ? " " : ",").Append((int)(mapping[i] >> 32)).Append(':').Append((int)(uint)mapping[i]);
            if (mapping.Count > 60) text.Append($" (+{mapping.Count - 60} more)");
            Mod.Log.Info(text.ToString());
        }

        private bool FitsRenderRails(MedianRibbon ribbon, MedianRenderRails first, MedianRenderRails second)
        {
            fittedSections.Clear();
            foreach (var section in sections)
            {
                bool near = section.Time <= 0.5f;
                float t = (near ? section.Time : 1f - section.Time) * 2f;
                (near ? first : second).Sample(t, out var a, out var b);
                if (!near) (a, b) = (b, a);
                if (math.distance(a, section.Left) > MedianRibbon.JoinTolerance ||
                    math.distance(b, section.Right) > MedianRibbon.JoinTolerance) return false;
                fittedSections.Add(new MedianSection(section.Time, (a + b) * 0.5f, (a - b) * 0.5f,
                    section.Direction, section.Curvature));
            }
            if (!MedianRibbon.ValidStrip(fittedSections)) return false;
            foreach (var lane in lanes)
                if (!MedianRibbon.ClearsLane(fittedSections, lane.Curve, lane.HalfWidth)) return false;
            return true;
        }

        private void SetTexture(Approach approach, MedianTextureHalf mapping)
        {
            if (approach.Starts)
            {
                if (EntityManager.HasComponent<MedianTextureHalf>(approach.Edge))
                    EntityManager.SetComponentData(approach.Edge, mapping);
                else EntityManager.AddComponentData(approach.Edge, mapping);
            }
            else
            {
                var end = new MedianEndTextureHalf { Mapping = mapping };
                if (EntityManager.HasComponent<MedianEndTextureHalf>(approach.Edge))
                    EntityManager.SetComponentData(approach.Edge, end);
                else EntityManager.AddComponentData(approach.Edge, end);
            }
        }

        private bool CheckNativeFit(MedianRibbon ribbon, NativeCurbFit aLeft, NativeCurbFit aRight,
            NativeCurbFit bLeft, NativeCurbFit bRight, out string reason)
        {
            fittedSections.Clear();
            foreach (var section in sections)
            {
                bool first = section.Time <= 0.5f;
                float localTime = (first ? section.Time : 1f - section.Time) * 2f;
                float3 left = (first ? aLeft : bLeft).Curb(localTime);
                float3 right = (first ? aRight : bRight).Curb(localTime);
                var across = (left - right) * 0.5f;
                float2 normal = section.Across.xz / ribbon.HalfWidth;
                float widthError = math.abs(2f * math.dot(across.xz, normal) - 2f * ribbon.HalfWidth);
                float skew = math.abs(math.dot(across.xz, section.Direction));
                float seamAllowance = MedianRibbon.SeamSkewTolerance * (1f - localTime);
                if (widthError > MedianRibbon.WidthTolerance || skew > MedianRibbon.WidthTolerance + seamAllowance ||
                    math.abs(math.dot(left.xz - section.Left.xz, normal)) > MedianRibbon.WidthTolerance ||
                    math.abs(math.dot(right.xz - section.Right.xz, normal)) > MedianRibbon.WidthTolerance)
                {
                    reason = $"the native node mesh cannot reproduce the constant-width curbs at {section.Time:F2} " +
                        $"(width error {widthError:F3} m, seam skew {skew:F3} m)";
                    return false;
                }
                fittedSections.Add(new MedianSection(section.Time, (left + right) * 0.5f, across,
                    section.Direction, section.Curvature));
            }

            if (!MedianRibbon.ValidStrip(fittedSections))
            {
                reason = "the native node mesh would fold the median";
                return false;
            }
            foreach (var lane in lanes)
            {
                if (MedianRibbon.ClearsLane(fittedSections, lane.Curve, lane.HalfWidth)) continue;
                reason = "the native node mesh would enter a travel lane";
                return false;
            }
            reason = null;
            return true;
        }

        private bool ReadRoad(Entity node, Entity edge, bool far, out Approach approach)
        {
            approach = default;
            if (!ReadApproach(node, edge, out bool starts, out var geometry) ||
                !EntityManager.TryGetComponent<Composition>(edge, out var composition) ||
                !EntityManager.TryGetComponent<EdgeGeometry>(edge, out var native) ||
                !EntityManager.TryGetComponent<NetCompositionData>(composition.m_Edge, out var edgeData) ||
                !EntityManager.HasBuffer<NetCompositionPiece>(composition.m_Edge)) return false;
            Entity nodeComposition = starts ? composition.m_StartNode : composition.m_EndNode;
            if (!EntityManager.TryGetComponent<NetCompositionData>(nodeComposition, out var data)) return false;
            foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(composition.m_Edge, true))
            {
                if (!NativeSections.IsDivider(piece)) continue;
                bool meshReady = curbWidths.TryGetValue(piece.m_Piece, out float halfWidth);
                if (!meshReady && EntityManager.HasBuffer<MeshVertex>(piece.m_Piece))
                {
                    halfWidth = ReadCurbHalfWidth(piece);
                    curbWidths[piece.m_Piece] = halfWidth;
                    tilePeriods[piece.m_Piece] = ReadTilePeriod(piece);
                    meshReady = true;
                }
                float pieceHalfWidth = piece.m_Size.x * 0.5f;
                if (!meshReady) halfWidth = pieceHalfWidth;
                float offset = piece.m_Offset.x * (starts ? -1f : 1f);
                float2 nodeWidths = data.m_Width * 0.5f + new float2(data.m_MiddleOffset, -data.m_MiddleOffset);
                if (halfWidth <= 0f || halfWidth >= math.cmin(nodeWidths) ||
                    math.abs(offset - data.m_MiddleOffset) > 0.1f) return false;
                var left = starts ? MathUtils.Invert(native.m_Start.m_Right) : native.m_End.m_Left;
                var right = starts ? MathUtils.Invert(native.m_Start.m_Left) : native.m_End.m_Right;
                float fraction = 0.5f + offset / edgeData.m_Width;
                float fractionWidth = halfWidth / edgeData.m_Width;
                float3 tangent = math.lerp(MathUtils.EndTangent(left), MathUtils.EndTangent(right), fraction);
                float3 leftCurb = math.lerp(left.d, right.d, fraction - fractionWidth);
                float3 rightCurb = math.lerp(left.d, right.d, fraction + fractionWidth);
                approach = new Approach
                {
                    Edge = edge,
                    Starts = starts,
                    Geometry = geometry,
                    NodeComposition = nodeComposition,
                    NodeData = data,
                    HalfWidth = halfWidth,
                    MarkerScale = pieceHalfWidth > halfWidth && pieceHalfWidth < math.cmin(nodeWidths)
                        ? pieceHalfWidth / halfWidth : 1f,
                    MouthStation = starts ? 0f : math.lerp((native.m_Start.m_Length + native.m_End.m_Length).x,
                        (native.m_Start.m_Length + native.m_End.m_Length).y, fraction),
                    Piece = piece.m_Piece,
                    MeshReady = meshReady,
                    EdgeComposition = composition.m_Edge,
                    DividerOffset = offset,
                    PieceHalfWidth = pieceHalfWidth,
                    Mouth = far ? new MedianMouth(rightCurb, leftCurb, -tangent) : new MedianMouth(leftCurb, rightCurb, tangent)
                };
                return true;
            }
            return false;
        }

        private float ReadCurbHalfWidth(NetCompositionPiece piece)
        {
            float halfWidth = piece.m_Size.x * 0.5f;
            var vertices = EntityManager.GetBuffer<MeshVertex>(piece.m_Piece, true);
            float flangeHeight = float.NegativeInfinity;
            foreach (var vertex in vertices)
                if (math.abs(vertex.m_Vertex.x) >= halfWidth - 0.01f)
                    flangeHeight = math.max(flangeHeight, vertex.m_Vertex.y);
            if (!math.isfinite(flangeHeight)) return halfWidth;
            var curb = float2.zero;
            foreach (var vertex in vertices)
            {
                if (vertex.m_Vertex.y <= flangeHeight + 0.02f) continue;
                curb = math.max(curb, new float2(-vertex.m_Vertex.x, vertex.m_Vertex.x));
            }
            return math.abs(curb.x - curb.y) <= 0.02f && curb.x >= halfWidth * 0.5f
                ? math.min(halfWidth, (curb.x + curb.y) * 0.5f) : halfWidth;
        }

        private float ReadTilePeriod(NetCompositionPiece piece)
        {
            if ((piece.m_PieceFlags & NetPieceFlags.DisableTiling) != 0 ||
                !EntityManager.HasBuffer<MeshUV0>(piece.m_Piece) || !EntityManager.HasBuffer<MeshIndex>(piece.m_Piece)) return 0f;
            var vertices = EntityManager.GetBuffer<MeshVertex>(piece.m_Piece, true);
            var indices = EntityManager.GetBuffer<MeshIndex>(piece.m_Piece, true);
            var uv = EntityManager.GetBuffer<MeshUV0>(piece.m_Piece, true);
            float2 total = 0f;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i].m_Index;
                int b = indices[i + 1].m_Index;
                int c = indices[i + 2].m_Index;
                float3 us = new float3(uv[a].m_Uv.y, uv[b].m_Uv.y, uv[c].m_Uv.y);
                float3 zs = new float3(vertices[a].m_Vertex.z, vertices[b].m_Vertex.z, vertices[c].m_Vertex.z);
                if (math.any(us < 0f) || math.any(zs < -0.01f)) continue;
                total += new float2(math.csum(math.abs(zs.yzx - zs)), math.csum(math.abs(us.yzx - us)));
            }
            float period = total.x / total.y;
            return math.isfinite(period) && period > 0.01f ? period : 0f;
        }

        private void ReadLanes(Entity node, ClosedMedian median, MedianMouth start, MedianMouth end, out Bezier4x3 guide)
        {
            lanes.Clear();
            float2 nearest = new float2(float.PositiveInfinity);
            Bezier4x3 forward = default;
            Bezier4x3 backward = default;
            foreach (var item in EntityManager.GetBuffer<SubLane>(node, true))
            {
                Entity lane = item.m_SubLane;
                if (EntityManager.HasComponent<MasterLane>(lane) || EntityManager.HasComponent<Deleted>(lane) ||
                    !EntityManager.TryGetComponent<CarLane>(lane, out var car) ||
                    !EntityManager.TryGetComponent<Lane>(lane, out var link) ||
                    !EntityManager.TryGetComponent<Curve>(lane, out var shape) ||
                    (car.m_Flags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0) continue;
                int from = link.m_StartNode.GetOwnerIndex();
                int to = link.m_EndNode.GetOwnerIndex();
                bool goesForward = from == median.FirstRoad.Index && to == median.SecondRoad.Index;
                bool goesBackward = from == median.SecondRoad.Index && to == median.FirstRoad.Index;
                if (!EntityManager.TryGetComponent<PrefabRef>(lane, out var prefab) ||
                    !EntityManager.TryGetComponent<NetLaneData>(prefab.m_Prefab, out var data) ||
                    !EntityManager.TryGetComponent<CarLaneData>(prefab.m_Prefab, out var carData) ||
                    (carData.m_RoadTypes & RoadTypes.Car) == 0) continue;
                float width = data.m_Width;
                if (EntityManager.TryGetComponent<NodeLane>(lane, out var nodeLane)) width += math.cmax(nodeLane.m_WidthOffset);
                lanes.Add(new TravelLane { Curve = shape.m_Bezier, HalfWidth = math.max(0f, width * 0.5f) });
                if (!goesForward && !goesBackward) continue;
                var curve = goesForward ? shape.m_Bezier : MathUtils.Invert(shape.m_Bezier);
                float distance = math.distancesq(curve.a.xz, start.Center.xz) + math.distancesq(curve.d.xz, end.Center.xz);
                if (goesForward && distance < nearest.x)
                {
                    nearest.x = distance;
                    forward = curve;
                }
                else if (goesBackward && distance < nearest.y)
                {
                    nearest.y = distance;
                    backward = curve;
                }
            }
            guide = math.isfinite(nearest.x)
                ? math.isfinite(nearest.y) ? MathUtils.Lerp(forward, backward, 0.5f) : forward
                : math.isfinite(nearest.y) ? backward : NetUtils.StraightCurve(start.Center, end.Center);
        }

        private static float2 Fractions(Approach approach)
        {
            var data = approach.NodeData;
            return approach.HalfWidth / (data.m_Width * 0.5f + new float2(data.m_MiddleOffset, -data.m_MiddleOffset));
        }

        // The marker moves to the piece edge only while its seam target stays
        // inside the rail pair. An outer rail that ends within one skirt width of
        // the curb leaves the marker on the curb face, which keeps the curb fit.
        private static float MarkerScaleFor(Approach approach, NativeCurbFit left, NativeCurbFit right) =>
            math.max(left.SeamFraction, right.SeamFraction) * approach.MarkerScale < 0.98f ? approach.MarkerScale : 1f;

        private void SetCurbMarkers(Entity composition, NetCompositionData data, float2 fractions,
            out float4 left, out float4 right)
        {
            // The game treats markers closer than this as one marker.
            const float snap = 0.00001f;
            float leftMarker = 1f - fractions.x;
            float rightMarker = fractions.y;
            left = math.select(data.m_SyncVertexOffsetsLeft, leftMarker, data.m_SyncVertexOffsetsLeft > leftMarker - snap);
            right = math.select(data.m_SyncVertexOffsetsRight, rightMarker, data.m_SyncVertexOffsetsRight < rightMarker + snap);
            left.w = leftMarker;
            right.x = rightMarker;
            if (math.all(left == data.m_SyncVertexOffsetsLeft) && math.all(right == data.m_SyncVertexOffsetsRight)) return;
            data.m_SyncVertexOffsetsLeft = left;
            data.m_SyncVertexOffsetsRight = right;
            EntityManager.SetComponentData(composition, data);
        }

        private void ReportMarkers(Entity node, float2 range, float tuck,
            float4 curbOffsets, float4 markerOffsets, float4 markerTargets)
        {
            uint state = math.hash(markerOffsets) ^ (31u * math.hash(markerTargets)) ^
                (397u * math.hash((int3)math.round(new float3(range, tuck) * 1000f)));
            if (reportedMarkers.TryGetValue(node, out uint reported) && reported == state) return;
            reportedMarkers[node] = state;
            Mod.Log.Info($"Median pavement markers at junction {node.Index}: curb offsets {curbOffsets.x:F4}/{curbOffsets.y:F4} " +
                $"and {curbOffsets.z:F4}/{curbOffsets.w:F4}, marker offsets {markerOffsets.x:F4}/{markerOffsets.y:F4} " +
                $"and {markerOffsets.z:F4}/{markerOffsets.w:F4}, seam targets {markerTargets.x:F4}/{markerTargets.y:F4} " +
                $"and {markerTargets.z:F4}/{markerTargets.w:F4}, pavement tucked {tuck * 100f:F1} cm, modelled pavement edge " +
                $"{range.x * 100f:F1} to {range.y * 100f:F1} cm from the divider skirt.");
        }

        private static void ApplyFits(ref EdgeNodeGeometry geometry, ref EdgeNodeGeometry street,
            NativeCurbFit left, NativeCurbFit right, float markerScale, float4 offsetsLeft, float4 offsetsRight)
        {
            float4 leftTargets = ProjectTargets(geometry.m_SyncVertexTargetsLeft,
                geometry.m_Left.m_Left.d, geometry.m_Left.m_Right.d, left.Inner.d);
            float4 rightTargets = ProjectTargets(1f - geometry.m_SyncVertexTargetsRight,
                geometry.m_Right.m_Right.d, geometry.m_Right.m_Left.d, right.Inner.d);

            // Side streets end at the curb face and keep their targets relative to it.
            float leftCurb = 1f - left.SeamFraction;
            float rightCurb = 1f - right.SeamFraction;
            street.m_SyncVertexTargetsLeft = math.min(leftCurb, leftTargets);
            street.m_SyncVertexTargetsRight = 1f - math.min(rightCurb, rightTargets);
            street.m_SyncVertexTargetsLeft.w = leftCurb;
            street.m_SyncVertexTargetsRight.x = right.SeamFraction;

            float leftMarker = 1f - left.MarkerSeamFraction(markerScale);
            float rightMarker = 1f - right.MarkerSeamFraction(markerScale);
            // A lane marker that shares the divider marker's offset shares its target.
            geometry.m_SyncVertexTargetsLeft = math.select(math.min(leftMarker, leftTargets), leftMarker,
                offsetsLeft >= offsetsLeft.w);
            geometry.m_SyncVertexTargetsRight = math.select(1f - math.min(rightMarker, rightTargets), 1f - rightMarker,
                offsetsRight <= offsetsRight.x);
            geometry.m_SyncVertexTargetsLeft.w = leftMarker;
            geometry.m_SyncVertexTargetsRight.x = 1f - rightMarker;
            geometry.m_Left.m_Right = left.Inner;
            geometry.m_Right.m_Left = right.Inner;
            geometry.m_Left.m_Length.y = MathUtils.Length(left.Inner);
            geometry.m_Right.m_Length.x = MathUtils.Length(right.Inner);
        }

        private void JoinSideStreets(Entity node, Entity first, Entity second,
            EdgeNodeGeometry a, EdgeNodeGeometry b, float3 aLeft, float3 aRight, float3 bLeft, float3 bRight,
            StripContext strip)
        {
            foreach (var connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connection.m_Edge;
                if (edge == first || edge == second || !ReadApproach(node, edge, out bool starts, out var street)) continue;
                bool left = JoinRoadSurface(ref street.m_Left, ref street.m_SyncVertexTargetsLeft,
                    a.m_Right, a.m_SyncVertexTargetsRight, aRight, false) ||
                    JoinRoadSurface(ref street.m_Left, ref street.m_SyncVertexTargetsLeft,
                        b.m_Right, b.m_SyncVertexTargetsRight, bRight, false);
                bool right = JoinRoadSurface(ref street.m_Right, ref street.m_SyncVertexTargetsRight,
                    a.m_Left, a.m_SyncVertexTargetsLeft, aLeft, true) ||
                    JoinRoadSurface(ref street.m_Right, ref street.m_SyncVertexTargetsRight,
                        b.m_Left, b.m_SyncVertexTargetsLeft, bLeft, true);
                if (left && right && math.distancesq(street.m_Left.m_Right.d, street.m_Right.m_Left.d) < 0.0001f)
                {
                    // A T-junction ends both side-street halves at the same curb.
                    // The road surface on either half must use the same middle rail.
                    var leftMiddle = street.m_Left.m_Right;
                    var rightMiddle = street.m_Right.m_Left;
                    var shared = MathUtils.Lerp(leftMiddle, rightMiddle, 0.5f);
                    leftMiddle.c = rightMiddle.c = shared.c;
                    leftMiddle.d = rightMiddle.d = shared.d;
                    street.m_Middle = shared;
                    street.m_Left.m_Right = leftMiddle;
                    street.m_Right.m_Left = rightMiddle;
                    street.m_Left.m_Length.y = MathUtils.Length(leftMiddle);
                    street.m_Right.m_Length.x = MathUtils.Length(rightMiddle);
                }
                if (left && right) MapStrip(strip, edge, starts, ref street);
                else StripOff(node, edge, starts, left || right ? "the side street meets only one half of the through road" : null);
                if (left || right) WriteApproach(edge, starts, street);
            }
        }

        // The road lanes beside the divider on one side of the ribbon, read from
        // the approach's own cross-section. Each lane's center is its distance
        // from the divider's center line.
        private bool LaneBand(Approach approach, bool far, float side, List<MedianRenderMesh.StripPiece> band,
            out float inner, out float outer)
        {
            band.Clear();
            inner = outer = approach.PieceHalfWidth;
            if (!EntityManager.HasBuffer<NetCompositionPiece>(approach.EdgeComposition)) return false;
            const NetPieceFlags lane = NetPieceFlags.HasRoadLanes | NetPieceFlags.Surface | NetPieceFlags.HasMesh;
            const NetSectionFlags apart = NetSectionFlags.Median | NetSectionFlags.Underground |
                NetSectionFlags.Overhead | NetSectionFlags.Hidden;
            // Across the approach the ribbon's left curb lies on the lower side
            // of the first road and on the higher side of the second.
            float toRibbon = (approach.Starts ? -1f : 1f) * (far ? 1f : -1f) * side;
            float divider = approach.DividerOffset * (far ? 1f : -1f) * side;
            laneScratch.Clear();
            foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(approach.EdgeComposition, true))
            {
                if ((piece.m_PieceFlags & (lane | NetPieceFlags.PreserveShape)) != lane ||
                    (piece.m_SectionFlags & apart) != 0) continue;
                float center = piece.m_Offset.x * toRibbon - divider;
                if (center <= 0f) continue;
                laneScratch.Add(new MedianRenderMesh.StripPiece
                {
                    Piece = piece.m_Piece, Size = piece.m_Size, Center = center,
                    Flags = piece.m_PieceFlags & ~(NetPieceFlags.HasRoadLanes | NetPieceFlags.HasBicycleLanes)
                });
            }
            laneScratch.Sort((x, y) => x.Center.CompareTo(y.Center));
            foreach (var piece in laneScratch)
            {
                if (math.abs(piece.Center - piece.Size.x * 0.5f - outer) > 0.05f) break;
                outer = piece.Center + piece.Size.x * 0.5f;
                band.Add(piece);
            }
            return band.Count != 0;
        }

        // Draws the through road's lanes across the junction on the side
        // street's side. The side street's own pavement fans from its mouth to
        // the median, so its surface runs across the through lanes and shows as
        // a triangle on them. The strip is a second layer just above it, carried
        // by the side street's junction mesh on that mesh's shape rails.
        private void MapStrip(StripContext context, Entity edge, bool starts, ref EdgeNodeGeometry street)
        {
            Entity node = context.Node;
            var ribbon = context.Ribbon;
            blockedStrips.Remove((edge, starts));
            bool foreign = foreignStreets.Contains((edge, starts));
            if ((foreign || MedianDiagnostics.StreetsNative) && EntityManager.TryGetComponent<Composition>(edge, out var own))
            {
                // A strip mesh left on the composition would reach the other
                // mod's copy, whose strip lanes have no rails.
                renderMesh.Restore(starts ? own.m_StartNode : own.m_EndNode);
                StripOff(node, edge, starts, foreign ? "another mod draws its junction mesh with its own copy"
                    : "the side street draws the game's own junction mesh");
                return;
            }
            if (!context.Dedicated)
            {
                // The divider waits for its own mesh, or the junction's native
                // deformation draws it and leaves no shape rails for the strip.
                bool native = renderMesh.IsNativeOnly(context.First.NodeComposition) ||
                    renderMesh.IsNativeOnly(context.Second.NodeComposition);
                StripOff(node, edge, starts, native ? "the median is drawn with the junction's native deformation" : null);
                return;
            }
            if (!MedianDiagnostics.Strip || !EntityManager.TryGetComponent<Composition>(edge, out var selected))
            {
                StripOff(node, edge, starts, MedianDiagnostics.Strip ? null : "the strip is switched off");
                return;
            }
            Entity composition = starts ? selected.m_StartNode : selected.m_EndNode;
            if (!EntityManager.TryGetComponent<NetCompositionData>(composition, out var data) ||
                (data.m_Flags.m_General & NativeSections.Closed) == 0)
            {
                StripOff(node, edge, starts, "the side street has no closed-median cross-section");
                return;
            }
            // A divided side street keeps its own median end (StemCompositionSystem).
            // Should its divider still reach the junction, the strip would cut
            // through it.
            if (EntityManager.HasBuffer<NetCompositionPiece>(composition))
                foreach (var piece in EntityManager.GetBuffer<NetCompositionPiece>(composition, true))
                    if (NativeSections.IsDivider(piece))
                    {
                        StripOff(node, edge, starts, "the side street's own median reaches into the junction");
                        return;
                    }

            float side = math.dot((street.m_Left.m_Right.a - ribbon.Offset(0.5f, 0f)).xz, ribbon.Normal(0.5f).xz) >= 0f
                ? 1f : -1f;
            if (!LaneBand(context.First, false, side, stripLanes, out float inner, out float outer) ||
                !LaneBand(context.Second, true, side, otherLanes, out float otherInner, out float otherOuter) ||
                stripLanes.Count != otherLanes.Count || math.abs(inner - otherInner) > 0.05f ||
                math.abs(outer - otherOuter) > 0.05f)
            {
                StripOff(node, edge, starts, "the two approaches have different lanes beside the median");
                return;
            }

            // The lanes' pieces are placed across the strip from its middle.
            float band = outer - inner;
            float middle = (inner + outer) * 0.5f;
            uint signature = math.hash(new float3(inner, outer, stripLanes.Count));
            for (int i = 0; i < stripLanes.Count; i++)
            {
                var lane = stripLanes[i];
                signature = signature * 31u + math.hash(new float4(lane.Piece.Index, lane.Center, lane.Size.x, lane.Size.z));
                lane.Center -= middle;
                stripLanes[i] = lane;
            }

            // The rows and columns of vertices at which the side street's
            // junction mesh will draw the strip's top layer.
            var lattice = stripLattice;
            var layout = renderMesh.StripLattice(composition, stripLanes, band, lattice, out float width,
                out float halfLength, out string refusal);
            if (layout == MedianRenderMesh.StripState.Pending)
            {
                // The game makes a new composition's mesh at the end of the frame.
                medians.WaitForMedianRenderMesh(node, composition, composition);
                StripOff(node, edge, starts, null);
                return;
            }
            if (layout != MedianRenderMesh.StripState.Ready)
            {
                StripOff(node, edge, starts, refusal);
                return;
            }

            // The strip's straight segments differ from the divider's along a
            // curve. Its inner edge reaches over the divider's skirt by that much,
            // and its inner steps stay on the rest of the skirt.
            float skirt = math.max(0f, inner - ribbon.HalfWidth);
            float overlap = NodeSurface.SkirtReach(ribbon, lattice, skirt);
            float edgeInner = inner - overlap;
            float innerTread = NodeSurface.InnerStepTread(skirt, InnerSteps, InnerTread);
            // The game clamps a piece that reaches past the side street's mesh
            // to its edge, where the strip's edge and its steps would collapse.
            float reach = band * 0.5f + math.max(OuterSteps * OuterTread, InnerSteps * innerTread);
            if (reach > width * 0.5f + 0.001f)
            {
                StripOff(node, edge, starts, $"the side street's junction mesh is {width:F1} m wide, and the strip " +
                    $"with its steps needs {reach * 2f:F1} m");
                return;
            }
            bool turned = NodeSurface.Turns(ribbon, side, edgeInner, outer, MedianDiagnostics.StripReverse);
            if (turned) lattice.Turn();
            lattice.Prepare(ribbon, side, edgeInner, outer);

            // Heights of the pavement that lies under the strip: the fanned part
            // of each through half on this side and the side street's own fan.
            // A T-junction's side street has one middle rail, which both of its
            // halves share. Its heights are settled together with the strip's.
            var middleRail = street.m_Left.m_Right;
            var otherMiddle = street.m_Right.m_Left;
            bool settles = MedianDiagnostics.StripSettle &&
                math.distancesq(middleRail.a, otherMiddle.a) < 0.0004f &&
                math.distancesq(middleRail.d, otherMiddle.d) < 0.0004f &&
                math.abs(middleRail.b.y - otherMiddle.b.y) < 0.005f && math.abs(middleRail.c.y - otherMiddle.c.y) < 0.005f;
            float3 evenGrade = (middleRail.d - middleRail.a) / 3f;
            float2 grade = new float2(middleRail.a.y + evenGrade.y, middleRail.d.y - evenGrade.y) -
                new float2(middleRail.b.y, middleRail.c.y);
            stripSamples.Clear();
            ThroughRise(ribbon, side, edgeInner, outer, lattice, context.A, context.First.NodeData,
                context.ASyncLeft, context.ASyncRight, NodeSurface.ThroughLeftHalf(side, false));
            ThroughRise(ribbon, side, edgeInner, outer, lattice, context.B, context.Second.NodeData,
                context.BSyncLeft, context.BSyncRight, NodeSurface.ThroughLeftHalf(side, true));
            NodeSurface.Rise(ribbon, side, edgeInner, outer, lattice, street.m_Left.m_Left, middleRail,
                data.m_SyncVertexOffsetsLeft, street.m_SyncVertexTargetsLeft, 0f, 1f, settles ? 1 : 0, stripSamples);
            NodeSurface.Rise(ribbon, side, edgeInner, outer, lattice, otherMiddle, street.m_Right.m_Right,
                data.m_SyncVertexOffsetsRight, street.m_SyncVertexTargetsRight, 0f, 1f, settles ? -1 : 0, stripSamples);
            bool fits = NodeSurface.Seat(stripSamples, lattice, MedianDiagnostics.StripClearance, MaximumStripLift,
                settles, grade, out var seating);
            if (!math.isnan(MedianDiagnostics.StripLift))
            {
                seating = new NodeSurface.Seating { Inner = MedianDiagnostics.StripLift, Outer = MedianDiagnostics.StripLift };
                fits = true;
            }
            float4 innerLift = seating.Inner;
            float4 outerLift = seating.Outer;
            if (MedianDiagnostics.Enabled)
                DiagnoseStreet(node, edge, starts, composition, data, street, side, seating);
            if (!fits || seating.Floating > MaximumStripFloat)
            {
                StripOff(node, edge, starts, "the junction surface is too uneven for a smooth strip " +
                    $"({seating.Floating * 100f:F1} cm between strip and pavement)");
                return;
            }
            // Each rail is one cubic, which strays from the ribbon on a sharp bend.
            // The strip was seated where the ribbon puts its vertices.
            NodeSurface.Rails(ribbon, side, edgeInner, outer, seating, lattice, MedianDiagnostics.StripReverse,
                out var innerRail, out var outerRail, out float drift);
            if (drift > MaximumRowDrift)
            {
                StripOff(node, edge, starts, $"the junction bends too sharply for the strip's rails " +
                    $"({drift * 100f:F1} cm off the seated vertex positions)");
                return;
            }

            // A smooth strip cannot follow every fold of the pavement, so parts
            // of its two long edges stand a few centimeters above it and cast a
            // thin shadow. Copies of the outer lane, each a little lower and a
            // little further out, end the outer edge in steps too low to show.
            // Copies of the inner lane do the same toward the median, over the
            // divider's skirt. The steps that reach below the pavement, into the
            // skirt or into the curb stay out of sight.
            var innerLane = stripLanes[0];
            var outerLane = stripLanes[stripLanes.Count - 1];
            for (int step = 1; step <= OuterSteps; step++)
            {
                var lower = outerLane;
                lower.Center += step * OuterTread;
                lower.Drop = step * StepRiser;
                stripLanes.Add(lower);
            }
            for (int step = 1; step <= InnerSteps; step++)
            {
                var lower = innerLane;
                lower.Center -= step * innerTread;
                lower.Drop = step * StepRiser;
                stripLanes.Add(lower);
            }
            signature = signature * 31u + math.hash(new float4(OuterSteps, OuterTread, InnerSteps, innerTread)) +
                math.hash(new float2(StepRiser, 1f));
            var state = renderMesh.TryGetStrip(composition, signature, stripLanes, width, halfLength, edge, starts,
                out Entity mesh);
            if (state == MedianRenderMesh.StripState.Pending)
            {
                // The mesh is made by the game's own factory over the next frames.
                medians.WaitForMedianRenderMesh(node, context.First.NodeComposition, context.Second.NodeComposition);
                StripOff(node, edge, starts, null);
                return;
            }
            if (state != MedianRenderMesh.StripState.Ready)
            {
                StripOff(node, edge, starts, "another closed junction draws its strip for other lanes or another " +
                    "skirt with this side street's junction mesh");
                blockedStrips[(edge, starts)] = new BlockedStrip { Node = node, Composition = composition, Signature = signature };
                return;
            }

            // The side street's pavement takes the heights the strip was seated on.
            street.m_Left.m_Right.b.y += seating.Settle.x;
            street.m_Left.m_Right.c.y += seating.Settle.y;
            street.m_Right.m_Left.b.y += seating.Settle.x;
            street.m_Right.m_Left.c.y += seating.Settle.y;
            street.m_Middle.b.y += seating.Settle.x;
            street.m_Middle.c.y += seating.Settle.y;

            float2 stations = turned ? context.Stations.yx : context.Stations;
            Include(ref street.m_Bounds, innerRail);
            Include(ref street.m_Bounds, outerRail);
            var mapping = new MedianStrip { Junction = node, Composition = composition, DedicatedMesh = mesh };
            new MedianRenderRails(innerRail, outerRail).Pack(width, 0f, halfLength, (outer - inner) * 0.5f,
                MathUtils.Center(street.m_Bounds),
                new float4(stations.x, math.lerp(stations.x, stations.y, 1f / 3f),
                    math.lerp(stations.x, stations.y, 2f / 3f), stations.y),
                out mapping.LeftRail, out mapping.RightRail);
            renderMesh.Assign(composition, mesh);
            MedianTextureSystem.SetStrip(EntityManager, edge, starts, mapping);

            // A fit that starts from its own result lands within a millimeter of it.
            uint report = math.hash(math.round(innerLift * 200f)) ^ (31u * math.hash(math.round(outerLift * 200f))) ^
                math.hash(new float4(inner, outer, side, math.round(overlap * 200f))) ^ (uint)mesh.Index;
            if (reportedStrips.TryGetValue(edge, out uint reported) && reported == report) return;
            reportedStrips[edge] = report;
            float2 mouths = new float2(math.max(innerLift.x, outerLift.x), math.max(innerLift.w, outerLift.w));
            Mod.Log.Info($"Through-lane strip at junction {node.Index} over side street {edge.Index}: " +
                $"{(side > 0f ? "left" : "right")} side, {inner:F2} to {outer:F2} m from the median's center, " +
                $"at most {seating.Floating * 100f:F1} cm above the pavement under it, {seating.Edge * 100f:F1} cm " +
                $"along its outer edge and {seating.Inside * 100f:F1} cm along its inner edge, " +
                $"{mouths.x * 100f:F1} and {mouths.y * 100f:F1} cm above the road at the two mouths, " +
                $"side street's middle line moved by {seating.Settle.x * 100f:F1} and {seating.Settle.y * 100f:F1} cm, " +
                $"{overlap * 100f:F1} cm over the divider's skirt.");
        }

        // Largest height of the strip above the ribbon's cross-section, and the
        // largest distance it may float above the pavement under it.
        private const float MaximumStripLift = 0.3f;
        private const float MaximumStripFloat = 0.08f;
        // Largest horizontal distance of a vertex of the strip from where the
        // strip was seated.
        private const float MaximumRowDrift = 0.08f;
        // The steps that end the strip's outer edge and its inner edge. Each
        // edge steps down as far as the strip may float, so no part of either
        // edge stands clear of the pavement. The inner steps are kept on the
        // divider's skirt.
        private const float StepRiser = 0.01f;
        private const int OuterSteps = 8;
        private const float OuterTread = 0.1f;
        private const int InnerSteps = 8;
        private const float InnerTread = 0.06f;

        private void ThroughRise(MedianRibbon ribbon, float side, float inner, float outer, NodeSurface.Lattice lattice,
            EdgeNodeGeometry geometry, NetCompositionData data, float4 syncLeft, float4 syncRight, bool leftSegment)
        {
            if (leftSegment)
                NodeSurface.Through(ribbon, side, inner, outer, lattice, geometry.m_Left.m_Left, geometry.m_Left.m_Right,
                    syncLeft, geometry.m_SyncVertexTargetsLeft, data.m_Width * 0.5f + data.m_MiddleOffset, true,
                    stripSamples);
            else
                NodeSurface.Through(ribbon, side, inner, outer, lattice, geometry.m_Right.m_Left, geometry.m_Right.m_Right,
                    syncRight, geometry.m_SyncVertexTargetsRight, data.m_Width * 0.5f - data.m_MiddleOffset, false,
                    stripSamples);
        }

        private static void Include(ref Bounds3 bounds, Bezier4x3 curve)
        {
            bounds.min = math.min(math.min(bounds.min, math.min(curve.a, curve.b)), math.min(curve.c, curve.d));
            bounds.max = math.max(math.max(bounds.max, math.max(curve.a, curve.b)), math.max(curve.c, curve.d));
        }

        // Leaves a side street without a strip. Its junction mesh may still be
        // one that carries strip lanes, now or once another junction with the
        // same composition gets its strip. The mapping stays, without rails, so
        // that the renderer visits this side street and hides those lanes.
        private void StripOff(Entity node, Entity edge, bool starts, string reason)
        {
            // A strip that was drawn here had settled the side street's middle
            // rail. Fit the junction again once the game has laid that rail anew.
            if (MedianTextureSystem.TryGetStrip(EntityManager, edge, starts, out var previous) &&
                previous.Junction == node && !previous.LeftRail.Equals(default(float4x4)))
                medians.RefreshJunction(node);
            Entity composition = EntityManager.TryGetComponent<Composition>(edge, out var selected)
                ? starts ? selected.m_StartNode : selected.m_EndNode : Entity.Null;
            MedianTextureSystem.SetStrip(EntityManager, edge, starts, new MedianStrip { Junction = node, Composition = composition });
            if (reason == null) return;
            uint report = (uint)reason.GetHashCode() | 1u;
            if (reportedStrips.TryGetValue(edge, out uint reported) && reported == report) return;
            reportedStrips[edge] = report;
            Mod.Log.Info($"No through-lane strip at junction {node.Index} over side street {edge.Index}: {reason}.");
        }

        // A junction whose median does not fit stays closed to traffic and is
        // unmarked on the next frame, which returns its side streets to their
        // regular compositions. A strip mapping that kept the rails of an earlier
        // fit would draw that strip until then, and would still count as drawing
        // the strip mesh, so another junction could not get one for its lanes.
        private void HideStrips(Entity node, Entity first, Entity second)
        {
            using (var edges = EntityManager.GetBuffer<ConnectedEdge>(node, true).ToNativeArray(Allocator.Temp))
            foreach (var connection in edges)
            {
                Entity edge = connection.m_Edge;
                if (edge == first || edge == second || !EntityManager.HasComponent<Composition>(edge) ||
                    !EntityManager.TryGetComponent<Game.Net.Edge>(edge, out var ends)) continue;
                if (ends.m_Start == node) StripOff(node, edge, true, null);
                if (ends.m_End == node) StripOff(node, edge, false, null);
            }
        }

        private struct BlockedStrip
        {
            public Entity Node;
            public Entity Composition;
            public uint Signature;
        }

        private void DiagnoseStreet(Entity node, Entity edge, bool starts, Entity composition, NetCompositionData data,
            EdgeNodeGeometry street, float side, NodeSurface.Seating seating)
        {
            float2 rise = new float2(float.PositiveInfinity, float.NegativeInfinity);
            foreach (var sample in stripSamples)
                rise = new float2(math.min(rise.x, sample.Height), math.max(rise.y, sample.Height));
            uint state = math.hash(math.round(seating.Inner * 200f)) ^ (31u * math.hash(math.round(seating.Outer * 200f))) ^
                math.hash(math.round(rise * 200f)) ^ math.hash(new float4(street.m_Left.m_Right.d, side));
            if (diagnosedStreets.TryGetValue(edge, out uint reported) && reported == state) return;
            diagnosedStreets[edge] = state;
            Mod.Log.Info($"DIAG street junction {node.Index} edge {edge.Index}: starts {starts} side {MedianDiagnostics.N(side)} " +
                $"composition {composition.Index} width {MedianDiagnostics.N(data.m_Width)} " +
                $"middleOffset {MedianDiagnostics.N(data.m_MiddleOffset)} " +
                $"offsetsL{MedianDiagnostics.V(data.m_SyncVertexOffsetsLeft)} offsetsR{MedianDiagnostics.V(data.m_SyncVertexOffsetsRight)} " +
                $"samples {stripSamples.Count} rise{MedianDiagnostics.V(rise)} innerLift{MedianDiagnostics.V(seating.Inner)} " +
                $"outerLift{MedianDiagnostics.V(seating.Outer)} settle{MedianDiagnostics.V(seating.Settle)} " +
                $"floating {MedianDiagnostics.N(seating.Floating)} edge {MedianDiagnostics.N(seating.Edge)} " +
                MedianDiagnostics.G(street));
        }

        private static bool JoinRoadSurface(ref Segment street, ref float4 targets,
            Segment divided, float4 dividedTargets, float3 curb, bool right)
        {
            var outer = right ? street.m_Right : street.m_Left;
            var oppositeOuter = right ? divided.m_Left : divided.m_Right;
            if (math.distancesq(outer.d, oppositeOuter.d) > 0.25f) return false;
            var inner = right ? street.m_Left : street.m_Right;
            inner.c += curb - inner.d;
            inner.d = curb;
            // The branch meets the curb face. Its asphalt ends before the restored
            // curb tread, which belongs to the through road's median.
            targets = right
                ? 1f - math.saturate(dividedTargets.wzyx / dividedTargets.w)
                : math.saturate((1f - dividedTargets.wzyx) / (1f - dividedTargets.x));
            if (right)
            {
                street.m_Left = inner;
                street.m_Length.x = MathUtils.Length(inner);
            }
            else
            {
                street.m_Right = inner;
                street.m_Length.y = MathUtils.Length(inner);
            }
            return true;
        }

        private static float4 ProjectTargets(float4 targets, float3 corner, float3 oldJoin, float3 newJoin)
        {
            float2 direction = (newJoin - corner).xz;
            float ratio = math.dot((oldJoin - corner).xz, direction) / math.max(0.0001f, math.lengthsq(direction));
            return math.saturate(targets * ratio);
        }

        private bool ReadApproach(Entity node, Entity edge, out bool starts, out EdgeNodeGeometry geometry)
        {
            starts = false;
            geometry = default;
            if (EntityManager.HasComponent<Deleted>(edge) || !EntityManager.TryGetComponent<Edge>(edge, out var ends))
                return false;
            starts = ends.m_Start == node;
            if (starts && EntityManager.TryGetComponent<StartNodeGeometry>(edge, out var start))
                geometry = start.m_Geometry;
            else if (ends.m_End == node && EntityManager.TryGetComponent<EndNodeGeometry>(edge, out var end))
                geometry = end.m_Geometry;
            else return false;
            return geometry.m_MiddleRadius == 0f;
        }

        private void WriteApproach(Entity edge, bool starts, EdgeNodeGeometry geometry)
        {
            if (starts) EntityManager.SetComponentData(edge, new StartNodeGeometry { m_Geometry = geometry });
            else EntityManager.SetComponentData(edge, new EndNodeGeometry { m_Geometry = geometry });
        }

        private struct Approach
        {
            internal Entity Edge;
            internal bool Starts;
            internal Entity NodeComposition;
            internal NetCompositionData NodeData;
            internal EdgeNodeGeometry Geometry;
            internal MedianMouth Mouth;
            internal float HalfWidth;
            internal float MarkerScale;
            internal float MouthStation;
            internal Entity Piece;
            internal bool MeshReady;
            internal Entity EdgeComposition;
            internal float DividerOffset;
            internal float PieceHalfWidth;
            internal bool Closed => (NodeData.m_Flags.m_General & NativeSections.Closed) != 0;
        }

        // What a side street needs to carry the through road's lanes across the junction.
        private struct StripContext
        {
            internal Entity Node;
            internal bool Dedicated;
            internal MedianRibbon Ribbon;
            internal Approach First;
            internal Approach Second;
            internal EdgeNodeGeometry A;
            internal EdgeNodeGeometry B;
            internal float4 ASyncLeft;
            internal float4 ASyncRight;
            internal float4 BSyncLeft;
            internal float4 BSyncRight;
            internal float2 Stations;
        }

        private struct TravelLane
        {
            internal Bezier4x3 Curve;
            internal float HalfWidth;
        }
    }
}
