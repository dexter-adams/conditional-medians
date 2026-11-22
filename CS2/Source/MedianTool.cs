using Colossal.Entities;
using Colossal.Mathematics;
using Colossal.UI.Binding;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.UI;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.InputSystem;
using Color = UnityEngine.Color;
using Layer = Game.Net.Layer;
using RaycastHit = Game.Common.RaycastHit;

namespace ConditionalMedians2
{
    // The median tool: hover a junction to see whether its median opening can
    // be closed, and click to close or reopen it. The game's own net raycast
    // finds the junction anywhere on its pavement or on a road just beside it,
    // and an outline on the median opening shows what a click will do. Right
    // click or the panel's close button returns to the default tool.
    public sealed partial class MedianToolSystem : ToolBaseSystem
    {
        public const string ToolId = "ConditionalMediansTool";
        // A road counts as pointing at its end junction within this distance of it.
        private const float RoadReach = 30f;
        private static readonly Color Closable = new Color(0.25f, 0.85f, 1f, 1f);
        private static readonly Color Reopen = new Color(1f, 0.7f, 0.2f, 1f);
        private static readonly Color Refused = new Color(1f, 0.3f, 0.2f, 1f);
        private MedianSystem medians;
        private OverlayRenderSystem overlay;

        public override string toolID => ToolId;
        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;
        internal bool IsActive => m_ToolSystem.activeTool == this;
        internal Entity Hovered { get; private set; }
        internal MedianSystem.JunctionView View { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
            overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
        }

        protected override void OnStopRunning()
        {
            base.OnStopRunning();
            applyAction.shouldBeEnabled = false;
            cancelAction.shouldBeEnabled = false;
            Hovered = Entity.Null;
            View = default;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road;
            // Ground only: a bridge over a junction must not hide it, and elevated
            // junctions cannot be closed anyway.
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround;
        }

        internal void Toggle() =>
            m_ToolSystem.activeTool = IsActive ? (ToolBaseSystem)m_DefaultToolSystem : this;

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            applyMode = ApplyMode.None;
            if (m_FocusChanged) return inputDeps;
            if (cancelAction.WasPressedThisFrame())
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return inputDeps;
            }
            Entity node = Entity.Null;
            // The panel can lie over a junction; clicks on it must not toggle what is beneath.
            if (!InputManager.instance.mouseOverUI && GetRaycastResult(out Entity hit, out RaycastHit info))
                node = JunctionOf(hit, info.m_HitPosition);
            Hovered = node;
            inputDeps.Complete();
            var buffer = overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
            // Every closure stays outlined while the tool is on, so the closed ones are easy to find.
            foreach (var pair in medians.ActiveJunctions)
                if (pair.Key != node) Outline(buffer, pair.Value, Faint, 0.15f);
            if (node == Entity.Null)
            {
                View = default;
                return default;
            }
            var view = medians.Describe(node);
            View = view;
            Draw(buffer, node, view);
            if (applyAction.WasPressedThisFrame() && CanToggle(view.State)) medians.RequestToggle(node);
            return default;
        }

        private static bool CanToggle(MedianSystem.JunctionState state) =>
            state != MedianSystem.JunctionState.Refused && state != MedianSystem.JunctionState.NotJunction &&
            state != MedianSystem.JunctionState.NoOpening;

        // The junction under the cursor: the node whose pavement was hit, or the
        // nearer end of a road hit close to that end.
        private Entity JunctionOf(Entity hit, float3 position)
        {
            if (hit == Entity.Null || !EntityManager.Exists(hit)) return Entity.Null;
            if (EntityManager.HasComponent<Node>(hit)) return hit;
            if (!EntityManager.TryGetComponent<Edge>(hit, out var ends)) return Entity.Null;
            Entity nearest = Entity.Null;
            float best = RoadReach * RoadReach;
            foreach (var end in new[] { ends.m_Start, ends.m_End })
            {
                if (!EntityManager.TryGetComponent<Node>(end, out var data)) continue;
                float distance = math.distancesq(data.m_Position.xz, position.xz);
                if (distance >= best) continue;
                best = distance;
                nearest = end;
            }
            return nearest;
        }

        private static readonly Color Neutral = new Color(0.75f, 0.8f, 0.85f, 0.6f);
        private static readonly Color Faint = new Color(1f, 0.7f, 0.2f, 0.45f);

        private void Draw(OverlayRenderSystem.Buffer buffer, Entity node, MedianSystem.JunctionView view)
        {
            var state = view.State;
            var color = state == MedianSystem.JunctionState.Closable ? Closable
                : state == MedianSystem.JunctionState.NoOpening ? Neutral
                : state == MedianSystem.JunctionState.Refused || state == MedianSystem.JunctionState.NotJunction ? Refused
                : Reopen;
            // The median opening, as the divider would close it.
            if (view.HasGeometry) Outline(buffer, view.Geometry, color, 0.35f);
            else if (EntityManager.TryGetComponent<Node>(node, out var data))
                buffer.DrawCircle(color, data.m_Position + new float3(0f, 0.3f, 0f), state == MedianSystem.JunctionState.NoOpening ? 3f : 6f);
            // The permitted turns that cross it, which the user must forbid first.
            if (view.Crossings != null)
                foreach (var curve in view.Crossings)
                    buffer.DrawCurve(Refused, curve + new float3(0f, 0.3f, 0f), 0.4f);
        }

        private static void Outline(OverlayRenderSystem.Buffer buffer, MedianGeometry median, Color color, float width)
        {
            float2 along = median.Axis * median.HalfLength;
            float2 across = new float2(-median.Axis.y, median.Axis.x) * math.max(median.HalfWidth, 0.5f);
            float3 Corner(float a, float b)
            {
                float2 xz = median.Center.xz + along * a + across * b;
                return new float3(xz.x, median.Center.y + 0.3f, xz.y);
            }
            var c0 = Corner(1, 1);
            var c1 = Corner(-1, 1);
            var c2 = Corner(-1, -1);
            var c3 = Corner(1, -1);
            buffer.DrawLine(color, new Line3.Segment(c0, c1), width);
            buffer.DrawLine(color, new Line3.Segment(c1, c2), width);
            buffer.DrawLine(color, new Line3.Segment(c2, c3), width);
            buffer.DrawLine(color, new Line3.Segment(c3, c0), width);
        }
    }

    // The hovered junction's state and the click's effect, at the cursor in
    // the game's own tooltip, so the eyes can stay on the junction.
    public sealed partial class MedianTooltipSystem : Game.UI.Tooltip.TooltipSystemBase
    {
        private MedianToolSystem tool;
        private Game.UI.Tooltip.StringTooltip state;
        private Game.UI.Tooltip.StringTooltip hint;

        protected override void OnCreate()
        {
            base.OnCreate();
            tool = World.GetOrCreateSystemManaged<MedianToolSystem>();
            state = new Game.UI.Tooltip.StringTooltip { path = "conditionalMediansState" };
            hint = new Game.UI.Tooltip.StringTooltip { path = "conditionalMediansHint" };
        }

        protected override void OnUpdate()
        {
            if (!tool.IsActive || tool.Hovered == Entity.Null) return;
            var view = tool.View;
            if (string.IsNullOrEmpty(view.Title)) return;
            var s = view.State;
            state.value = Game.UI.Localization.LocalizedString.Value(view.Title);
            state.color = s == MedianSystem.JunctionState.Refused ? Game.UI.Tooltip.TooltipColor.Error
                : s == MedianSystem.JunctionState.Closable || s == MedianSystem.JunctionState.NoOpening ? Game.UI.Tooltip.TooltipColor.Info
                : Game.UI.Tooltip.TooltipColor.Warning;
            AddMouseTooltip(state);
            string text = s == MedianSystem.JunctionState.Closable ? "Click to close the median opening."
                : s == MedianSystem.JunctionState.Refused ? view.Detail
                : s == MedianSystem.JunctionState.NoOpening || s == MedianSystem.JunctionState.NotJunction ? null
                : "Click to reopen the median.";
            if (string.IsNullOrEmpty(text)) return;
            hint.value = Game.UI.Localization.LocalizedString.Value(text);
            hint.color = Game.UI.Tooltip.TooltipColor.Info;
            AddMouseTooltip(hint);
        }
    }

    // The panel's data and actions, bound to the UI module under the mod's id.
    public sealed partial class MedianUISystem : UISystemBase
    {
        internal const string Group = "ConditionalMedians2";
        private MedianToolSystem tool;
        private MedianSystem medians;

        protected override void OnCreate()
        {
            base.OnCreate();
            tool = World.GetOrCreateSystemManaged<MedianToolSystem>();
            medians = World.GetOrCreateSystemManaged<MedianSystem>();
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "ToolActive", () => tool.IsActive));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "HoverState",
                () => tool.Hovered == Entity.Null ? "None" : tool.View.State.ToString()));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "HoverTitle",
                () => tool.Hovered == Entity.Null ? "" : tool.View.Title ?? ""));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "HoverDetail",
                () => tool.Hovered == Entity.Null ? "" : tool.View.Detail ?? ""));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "Status", () => medians.Status ?? ""));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "ClosedCount", () => medians.ClosedCount));
            AddBinding(new TriggerBinding(Group, "ToggleTool", () => tool.Toggle()));
        }

        protected override void OnUpdate()
        {
            // F8 still opens and closes the tool, as it showed and hid the old panel.
            var keyboard = Keyboard.current;
            var game = Game.SceneFlow.GameManager.instance;
            if (keyboard != null && keyboard.f8Key.wasPressedThisFrame && game != null && game.gameMode == Game.GameMode.Game &&
                !game.isGameLoading && InputManager.instance != null && !InputManager.instance.hasInputFieldFocus)
                tool.Toggle();
            base.OnUpdate();
        }
    }
}
