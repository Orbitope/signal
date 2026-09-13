using Godot;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// Draws the road network and signal state; owns the meters→pixels mapping
    /// that every other view uses. Right-hand traffic: each directed link is
    /// offset to its travel-direction right so opposing links sit side by side.
    /// </summary>
    public partial class NetworkView : Node2D
    {
        [Export] public float PixelsPerMeter = 2.2f;
        [Export] public float LaneOffset = 2.2f;      // meters, perpendicular shift

        public SimRunner Runner;

        // Orbitope tokens. Stress on red bars ramps toward AMBER (friction),
        // never coral — coral is reserved for spillback (see FlashSpillback).
        private static readonly Color RoadColor = Orbitope.Raised;
        private static readonly Color GreenBar = Orbitope.LightGreen;
        private static readonly Color RedBar = Orbitope.LightRed;
        private static readonly Color YellowBar = Orbitope.LightYellow;
        private static readonly Color StressTint = Orbitope.AmberBright;

        // Spillback pulses: linkId -> remaining flash time.
        private readonly System.Collections.Generic.Dictionary<int, float> _spillFlash = new();
        private const float SpillFlashDuration = 1.4f;

        public void FlashSpillback(int linkId) => _spillFlash[linkId] = SpillFlashDuration;

        public Vector2 ToWorld(float x, float y) => new(x * PixelsPerMeter, -y * PixelsPerMeter);

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
        private readonly System.Collections.Generic.List<int> _flashKeys = new();

        public override void _Draw()
        {
            if (Runner?.Sim == null) return;
            var net = Runner.Sim.Network;

            foreach (var link in net.Links)
            {
                var (a, b) = LinkLine(link);
                DrawLine(a, b, RoadColor, 5.5f * PixelsPerMeter / 2.2f);
            }

            // Spillback: the scene's single coral element — a pulse along the
            // blocked link, fading out.
            foreach (var kv in _spillFlash)
            {
                var link = net.LinkById(kv.Key);
                var (a, b) = LinkLine(link);
                float t = kv.Value / SpillFlashDuration;
                var c = Orbitope.Coral; c.A = 0.25f + 0.6f * t;
                DrawLine(a, b, c, (6.5f + 3f * t) * PixelsPerMeter / 2.2f);
            }

            // Stop bars at signalized nodes, stress-tinted by head-of-queue wait.
            foreach (var node in net.Nodes)
            {
                if (node.Control is not SignalController ctl) continue;
                foreach (int inId in node.InLinks)
                {
                    var link = net.LinkById(inId);
                    var (a, b) = LinkLine(link);
                    var dir = (b - a).Normalized();
                    var right = new Vector2(-dir.Y, dir.X);
                    var barCenter = b - dir * 4f;
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
                    DrawLine(barCenter - right * 6f, barCenter + right * 6f, c, 3.5f);
                }
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
            float best = 18f * PixelsPerMeter;   // pick radius in px
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
