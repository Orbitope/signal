using Godot;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// Draws the road network and signal state; owns the meters→pixels mapping
    /// that every other view uses. Right-hand traffic: each directed link is
    /// offset to its travel-direction right so opposing links sit side by side.
    ///
    /// Sizes are zoom-aware: a road, stop bar, or ring is drawn at its real
    /// world size when zoomed in, but never below a minimum on-screen size, so
    /// a 25-signal map fit to the window still reads instead of collapsing
    /// into hairlines.
    /// </summary>
    public partial class NetworkView : Node2D
    {
        [Export] public float PixelsPerMeter = 2.2f;
        [Export] public float LaneOffset = 2.2f;        // meters, perpendicular shift
        [Export] public float LaneWidthMeters = 3.6f;   // drawn width of one directed link

        public SimRunner Runner;

        // Orbitope tokens. The road body sits between Raised and Border so it
        // reads against Void at any zoom. Stress on red bars ramps toward AMBER
        // (friction), never coral — coral is reserved for spillback.
        private static readonly Color RoadColor = Orbitope.Raised.Lerp(Orbitope.Border, 0.55f);
        private static readonly Color GreenBar = Orbitope.LightGreen;
        private static readonly Color RedBar = Orbitope.LightRed;
        private static readonly Color YellowBar = Orbitope.LightYellow;
        private static readonly Color StressTint = Orbitope.AmberBright;

        // Spillback pulses: linkId -> remaining flash time.
        private readonly Dictionary<int, float> _spillFlash = new();
        private readonly List<int> _flashKeys = new();
        private const float SpillFlashDuration = 1.4f;

        public void FlashSpillback(int linkId) => _spillFlash[linkId] = SpillFlashDuration;

        public Vector2 ToWorld(float x, float y) => new(x * PixelsPerMeter, -y * PixelsPerMeter);

        /// <summary>Camera zoom (world px → screen px). 1 when there is no camera.</summary>
        public float Zoom => GetViewport()?.GetCamera2D()?.Zoom.X ?? 1f;

        /// <summary>A world size that never renders below minScreenPx on screen.</summary>
        public float Legible(float worldPx, float minScreenPx)
            => Mathf.Max(worldPx, minScreenPx / Mathf.Max(Zoom, 0.01f));

        /// <summary>World-space endpoints of a link's drawn centerline. Base
        /// offset separates opposing directions (right-hand traffic); parallel
        /// lane-links in the same bundle (same from/to) fan out by lane index
        /// so bays and through lanes render side by side.</summary>
        public (Vector2 a, Vector2 b) LinkLine(Link link)
        {
            var net = Runner.Sim.Network;
            var from = net.NodeById(link.From);
            var to = net.NodeById(link.To);
            var a = ToWorld(from.X, from.Y);
            var b = ToWorld(to.X, to.Y);
            var dir = (b - a).Normalized();
            var right = new Vector2(-dir.Y, dir.X);   // screen-space right of travel

            // Lane index within its (from,to) bundle, deterministic by link id.
            int laneIdx = 0, laneCount = 0;
            foreach (var other in net.Links)
                if (other.From == link.From && other.To == link.To)
                { if (other.Id < link.Id) laneIdx++; laneCount++; }

            float lane = laneCount > 1 ? laneIdx - (laneCount - 1) * 0.5f : 0f;
            var off = right * (LaneOffset + lane * LaneOffset * 1.15f) * PixelsPerMeter;
            return (a + off, b + off);
        }

        public Vector2 PosOnLink(Link link, float pos)
        {
            var (a, b) = LinkLine(link);
            return a.Lerp(b, Mathf.Clamp(pos / link.Length, 0f, 1f));
        }

        public override void _Process(double delta)
        {
            if (_spillFlash.Count > 0)
            {
                _flashKeys.Clear();
                _flashKeys.AddRange(_spillFlash.Keys);
                foreach (var k in _flashKeys)
                {
                    _spillFlash[k] -= (float)delta;
                    if (_spillFlash[k] <= 0f) _spillFlash.Remove(k);
                }
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (Runner?.Sim == null) return;
            var net = Runner.Sim.Network;
            float roadW = Legible(LaneWidthMeters * PixelsPerMeter, 3.5f);

            foreach (var link in net.Links)
            {
                var (a, b) = LinkLine(link);
                DrawLine(a, b, RoadColor, roadW);
            }

            // Spillback: the scene's single coral element — a pulse along the
            // blocked link, fading out.
            foreach (var kv in _spillFlash)
            {
                var link = net.LinkById(kv.Key);
                var (a, b) = LinkLine(link);
                float t = kv.Value / SpillFlashDuration;
                var c = Orbitope.Coral; c.A = 0.25f + 0.6f * t;
                DrawLine(a, b, c, Legible((6.5f + 3f * t) * PixelsPerMeter / 2.2f, 4f));
            }

            // Stop bars at signalized nodes, stress-tinted by head-of-queue wait.
            float half = Legible(6f, 5f), back = Legible(4f, 3f), barW = Legible(3.5f, 2.5f);
            foreach (var node in net.Nodes)
            {
                if (node.Control is not SignalController ctl) continue;
                foreach (int inId in node.InLinks)
                {
                    var link = net.LinkById(inId);
                    var (a, b) = LinkLine(link);
                    var dir = (b - a).Normalized();
                    var right = new Vector2(-dir.Y, dir.X);
                    var barCenter = b - dir * back;
                    Color c = RedBar;
                    if (ctl.State == SignalState.Yellow) c = YellowBar;
                    else if (ctl.State == SignalState.Green && MovementAllowedFrom(node, ctl, inId))
                        c = GreenBar;
                    // Stress tint: how long has the head vehicle been waiting?
                    var front = link.Front;
                    if (front != null && c.R < 0.5f)   // only tint reds
                    {
                        float stress = Mathf.Clamp(front.Wait / 45f, 0f, 1f);
                        c = c.Lerp(StressTint, stress * 0.8f);
                    }
                    DrawLine(barCenter - right * half, barCenter + right * half, c, barW);
                }
                // Player override: an amber ring on a light being held by a tap.
                if (Runner.IsOverriding(node.Id))
                    DrawArc(ToWorld(node.X, node.Y), Legible(9f * PixelsPerMeter / 2.2f, 9f),
                            0f, Mathf.Tau, 32, Orbitope.AmberBright, Legible(2.5f, 2f));
            }
        }

        private static bool MovementAllowedFrom(Signal.Core.Node node, SignalController ctl, int inLink)
        {
            var phase = ctl.Phases[ctl.CurrentPhase];
            foreach (int mi in phase.movements)
                if (node.Movements[mi].InLink == inLink) return true;
            return false;
        }

        /// <summary>Hit test a click to the nearest signalized approach (tap zone
        /// = last 30 m of an in-link). Returns false if nothing close enough.</summary>
        public bool TryPickApproach(Vector2 worldPos, out int nodeId, out int inLinkId)
        {
            nodeId = -1; inLinkId = -1;
            float best = Legible(18f * PixelsPerMeter, 16f);   // pick radius, world px
            var net = Runner.Sim.Network;
            foreach (var node in net.Nodes)
            {
                if (node.Control is not SignalController) continue;
                foreach (int inId in node.InLinks)
                {
                    var link = net.LinkById(inId);
                    var zoneStart = PosOnLink(link, Mathf.Max(0, link.Length - 30f));
                    var zoneEnd = PosOnLink(link, link.Length);
                    float d = DistToSegment(worldPos, zoneStart, zoneEnd);
                    if (d < best) { best = d; nodeId = node.Id; inLinkId = inId; }
                }
            }
            return nodeId >= 0;
        }

        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
            return p.DistanceTo(a + ab * t);
        }
    }
}
