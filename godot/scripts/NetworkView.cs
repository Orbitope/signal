using Godot;
using System.Collections.Generic;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// Draws the road network, every junction's control (signal bars, stop
    /// octagons, yield triangles), compass labels at the map edges, and the
    /// current selection; owns the meters→pixels mapping every other view
    /// uses. Right-hand traffic: each directed link is offset to its
    /// travel-direction right so opposing links sit side by side.
    ///
    /// Legibility rules: sizes are zoom-aware (never below a minimum on-screen
    /// size), signal colours are saturated, and each control type has a
    /// distinct real-world shape so a player can read the junction at a glance.
    /// </summary>
    public partial class NetworkView : Node2D
    {
        [Export] public float PixelsPerMeter = 2.2f;
        [Export] public float LaneOffset = 2.2f;        // meters, perpendicular shift
        [Export] public float LaneWidthMeters = 3.6f;   // drawn width of one directed link
        [Export] public bool ShowCompass = true;

        public SimRunner Runner;

        /// <summary>Editor: draw the grid and the document's own geometry, so
        /// the map is visible even while the level is not yet buildable.</summary>
        public EditorDoc Overlay;
        public bool ShowGrid;

        /// <summary>Selection highlight (puzzle editing). -1 = none.</summary>
        public int SelectedNode = -1, SelectedLink = -1;
        /// <summary>Extra highlight point (e.g. a roundabout's original centre).</summary>
        public Vector2? SelectedPoint;

        // Road palette: lighter than the void so the network reads at any zoom.
        private static readonly Color RoadEdge = new("15140F");
        private static readonly Color RoadBody = new("46433A");
        private static readonly Color GreenBar = new("5FD068");
        private static readonly Color YellowBar = new("F0B040");
        private static readonly Color RedBar = new("E8503C");
        private static readonly Color StopSign = new("D8372B");
        private static readonly Color SignEdge = new("F4EFE2");
        private static readonly Color Highlight = Orbitope.AmberBright;

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

            int laneIdx = 0, laneCount = 0;
            foreach (var other in net.Links)
                if (other.From == link.From && other.To == link.To)
                { if (other.Id < link.Id) laneIdx++; laneCount++; }

            float lane = laneCount > 1 ? laneIdx - (laneCount - 1) * 0.5f : 0f;
            var off = right * (LaneOffset + lane * LaneOffset * 1.15f) * PixelsPerMeter;
            return (a + off, b + off);
        }

        public Vector2 PosOnLink(Link link, float pos) => Pose(link, pos).p;

        // Roundabout arcs: a link between two yield-entry nodes is drawn (and
        // driven) as a circular arc about the ring's centre, not a chord.
        private readonly Dictionary<int, (Vector2 c, float r, float a0, float a1)> _arcs = new();
        private RoadNetwork _arcsFor;

        private void EnsureArcs(RoadNetwork net)
        {
            if (_arcsFor == net) return;
            _arcsFor = net; _arcs.Clear();
            // Union yield nodes joined by links into rings; centre = centroid.
            var parent = new Dictionary<int, int>();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            foreach (var n in net.Nodes) if (n.Control is YieldEntryControl) parent[n.Id] = n.Id;
            foreach (var l in net.Links)
                if (parent.ContainsKey(l.From) && parent.ContainsKey(l.To)) parent[Find(l.From)] = Find(l.To);
            var sum = new Dictionary<int, (Vector2 s, int k)>();
            foreach (var id in parent.Keys)
            {
                int r = Find(id); var n = net.NodeById(id);
                sum.TryGetValue(r, out var acc);
                sum[r] = (acc.s + ToWorld(n.X, n.Y), acc.k + 1);
            }
            foreach (var l in net.Links)
            {
                if (!parent.ContainsKey(l.From) || !parent.ContainsKey(l.To)) continue;
                var (sv, k) = sum[Find(l.From)];
                if (k < 3) continue;
                var c = sv / k;
                var pa = ToWorld(net.NodeById(l.From).X, net.NodeById(l.From).Y);
                var pb = ToWorld(net.NodeById(l.To).X, net.NodeById(l.To).Y);
                float r = (pa.DistanceTo(c) + pb.DistanceTo(c)) * 0.5f + LaneOffset * PixelsPerMeter;
                float a0 = (pa - c).Angle(), a1 = (pb - c).Angle();
                // Screen y is down, so counter-clockwise in the world is clockwise on screen: angles increase.
                while (a1 <= a0) a1 += Mathf.Tau;
                _arcs[l.Id] = (c, r, a0, a1);
            }
        }

        public bool IsArc(Link link) { EnsureArcs(Runner.Sim.Network); return _arcs.ContainsKey(link.Id); }

        /// <summary>Position and travel direction at `pos` metres along a link.</summary>
        public (Vector2 p, Vector2 dir) Pose(Link link, float pos)
        {
            EnsureArcs(Runner.Sim.Network);
            float t = Mathf.Clamp(pos / link.Length, 0f, 1f);
            if (_arcs.TryGetValue(link.Id, out var arc))
            {
                float ang = Mathf.Lerp(arc.a0, arc.a1, t);
                var radial = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                return (arc.c + radial * arc.r, new Vector2(-radial.Y, radial.X));
            }
            var (a, b) = LinkLine(link);
            return (a.Lerp(b, t), (b - a).Normalized());
        }

        private void DrawRoad(Link link, Color color, float width)
        {
            if (_arcs.TryGetValue(link.Id, out var arc))
                DrawArc(arc.c, arc.r, arc.a0, arc.a1, 24, color, width);
            else { var (a, b) = LinkLine(link); DrawLine(a, b, color, width); }
        }

        /// <summary>A junction a player can edit: not a map edge, not a
        /// roundabout entry, not a lane fork — a real crossroads or T.</summary>
        public static bool IsEditable(Signal.Core.Node n)
            => !n.IsBoundary && !(n.Control is YieldEntryControl) && n.InLinks.Count >= 3;

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

        private void DrawEditorLayer()
        {
            if (Overlay == null) return;
            float s = Overlay.spacing * PixelsPerMeter;
            if (ShowGrid)
            {
                var dot = Orbitope.Border with { A = 0.9f };
                float r = Legible(1.2f * PixelsPerMeter, 3f);
                for (int gx = 0; gx < Overlay.cols; gx++)
                    for (int gy = 0; gy < Overlay.rows; gy++)
                        DrawCircle(new Vector2(gx, gy) * s, r, dot);
            }
            // The document's geometry under the (possibly absent) built roads.
            var ghost = Orbitope.Border with { A = 0.7f };
            float w = Legible(2.5f * PixelsPerMeter, 6f);
            foreach (var st in Overlay.streets)
                DrawLine(new Vector2(st.ax, st.ay) * s, new Vector2(st.bx, st.by) * s, ghost, w);
            foreach (var j in Overlay.junctions)
            {
                var c = new Vector2(j.gx, j.gy) * s;
                for (int d = 0; d < 4; d++)
                    if (j.Open(d) && Overlay.NeighbourVia(j, d, out _) == null)
                    {
                        var dir = new Vector2(EditorDoc.DX[d], EditorDoc.DY[d]);
                        var end = c + dir * Overlay.stub * PixelsPerMeter;
                        DrawLine(c, end, ghost, w);
                        float a = Legible(3f * PixelsPerMeter, 8f);
                        var right = new Vector2(-dir.Y, dir.X);
                        DrawColoredPolygon(new[] { end + dir * a, end - dir * a * 0.4f + right * a * 0.8f, end - dir * a * 0.4f - right * a * 0.8f }, Orbitope.TextMuted);
                    }
                float half = Legible(4f * PixelsPerMeter, 9f);
                DrawRect(new Rect2(c - new Vector2(half, half), new Vector2(2f * half, 2f * half)), Orbitope.Raised.Lerp(Orbitope.Border, 0.5f));
                DrawRect(new Rect2(c - new Vector2(half, half), new Vector2(2f * half, 2f * half)), Orbitope.TextMuted, false, Legible(0.6f, 1.5f));
            }
        }

        public override void _Draw()
        {
            DrawEditorLayer();
            if (Runner?.Sim == null) return;
            var net = Runner.Sim.Network;
            EnsureArcs(net);
            float roadW = Legible(LaneWidthMeters * PixelsPerMeter, 13f);
            float edgeW = roadW + Legible(1.2f, 3f);

            foreach (var link in net.Links) DrawRoad(link, RoadEdge, edgeW);
            foreach (var link in net.Links) DrawRoad(link, RoadBody, roadW);

            // Direction ticks: a faint chevron every 40 m so one-way streets read.
            float tick = Legible(1.6f * PixelsPerMeter, 3f);
            var tickColor = RoadEdge with { A = 0.9f };
            foreach (var link in net.Links)
            {
                if (_arcs.ContainsKey(link.Id)) continue;
                var (a, b) = LinkLine(link);
                var dir = (b - a).Normalized();
                var right = new Vector2(-dir.Y, dir.X);
                for (float m = 30f; m < link.Length - 20f; m += 40f)
                {
                    var p = a.Lerp(b, m / link.Length);
                    DrawLine(p - dir * tick - right * tick, p, tickColor, Legible(0.8f, 1.5f));
                    DrawLine(p - dir * tick + right * tick, p, tickColor, Legible(0.8f, 1.5f));
                }
            }

            // Spillback: a coral pulse along the blocked link, fading out.
            foreach (var kv in _spillFlash)
            {
                var link = net.LinkById(kv.Key);
                float t = kv.Value / SpillFlashDuration;
                var c = Orbitope.Coral; c.A = 0.25f + 0.6f * t;
                DrawRoad(link, c, roadW * (1f + 0.4f * t));
            }

            // Selection highlight under the badges.
            if (SelectedLink >= 0 && net.TryLink(SelectedLink, out var sl))
                DrawRoad(sl, Highlight with { A = 0.85f }, roadW * 1.35f);
            if (SelectedNode >= 0 && net.TryNode(SelectedNode, out var sn))
                DrawArc(ToWorld(sn.X, sn.Y), Legible(11f * PixelsPerMeter, 26f), 0f, Mathf.Tau, 48, Highlight, Legible(2f, 3f));
            if (SelectedPoint.HasValue)
                DrawArc(SelectedPoint.Value, Legible(24f * PixelsPerMeter, 30f), 0f, Mathf.Tau, 48, Highlight, Legible(2f, 3f));

            // Controls at every junction. A dark disc under a junction keeps the
            // bars from tangling in the middle; signal state is colour AND
            // shape (solid = go, broken = stop, thin = clearing) for
            // colour-blind players.
            float half = Legible(2.4f * PixelsPerMeter, 13f), back = Legible(2f * PixelsPerMeter, 8f), barW = Legible(1.6f * PixelsPerMeter, 6f);
            foreach (var node in net.Nodes)
            {
                if (node.IsBoundary || node.InLinks.Count < 3) continue;
                DrawCircle(ToWorld(node.X, node.Y), Legible(3.2f * PixelsPerMeter, 10f), RoadEdge);
            }
            foreach (var node in net.Nodes)
            {
                if (node.IsBoundary) continue;
                switch (node.Control)
                {
                    case SignalController ctl:
                        foreach (int inId in node.InLinks)
                        {
                            var link = net.LinkById(inId);
                            var (a, b) = LinkLine(link);
                            var dir = (b - a).Normalized();
                            var right = new Vector2(-dir.Y, dir.X);
                            var barCenter = b - dir * back;
                            bool served = MovementServedFrom(node, ctl, inId);
                            if (served && ctl.State == SignalState.Green)
                                DrawLine(barCenter - right * half, barCenter + right * half, GreenBar, barW);
                            else if (served && ctl.State == SignalState.Yellow)
                                DrawLine(barCenter - right * half, barCenter + right * half, YellowBar, barW * 0.55f);
                            else
                            {   // red: a broken bar
                                DrawLine(barCenter - right * half, barCenter - right * half * 0.25f, RedBar, barW);
                                DrawLine(barCenter + right * half * 0.25f, barCenter + right * half, RedBar, barW);
                            }
                        }
                        if (Runner.IsOverriding(node.Id))
                            DrawArc(ToWorld(node.X, node.Y), Legible(9f * PixelsPerMeter, 14f),
                                    0f, Mathf.Tau, 32, Orbitope.AmberBright, Legible(2.5f, 3f));
                        break;
                    case AllWayStopControl:
                        foreach (int inId in node.InLinks) DrawStopSign(net.LinkById(inId), back);
                        break;
                    case TwoWayStopControl tw:
                        foreach (int inId in node.InLinks)
                            if (!tw.MajorInLinks.Contains(inId)) DrawStopSign(net.LinkById(inId), back);
                        break;
                    case YieldEntryControl y:
                        foreach (int inId in node.InLinks)
                            if (!y.PriorityInLinks.Contains(inId)) DrawYield(net.LinkById(inId), back);
                        break;
                }
            }

            if (ShowCompass) DrawCompass(net);
        }

        private void DrawStopSign(Link link, float back)
        {
            var (a, b) = LinkLine(link);
            var dir = (b - a).Normalized();
            var right = new Vector2(-dir.Y, dir.X);
            var c = b - dir * back + right * Legible(2.6f * PixelsPerMeter, 12f);
            float r = Legible(1.9f * PixelsPerMeter, 10f);
            var pts = new Vector2[8];
            for (int i = 0; i < 8; i++)
            {
                float ang = Mathf.Tau * i / 8f + Mathf.Pi / 8f;
                pts[i] = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r;
            }
            DrawColoredPolygon(pts, StopSign);
            var outline = new Vector2[9]; pts.CopyTo(outline, 0); outline[8] = pts[0];
            DrawPolyline(outline, SignEdge, Legible(0.5f, 1.5f));
            // Stop line across the lane.
            var barCenter = b - dir * back;
            float half = Legible(2.2f * PixelsPerMeter, 12f);
            DrawLine(barCenter - right * half, barCenter + right * half, SignEdge with { A = 0.85f }, Legible(0.9f, 3f));
        }

        private void DrawYield(Link link, float back)
        {
            var (a, b) = LinkLine(link);
            var dir = (b - a).Normalized();
            var right = new Vector2(-dir.Y, dir.X);
            var c = b - dir * back + right * Legible(2.6f * PixelsPerMeter, 12f);
            float r = Legible(2.1f * PixelsPerMeter, 11f);
            // Inverted triangle: point toward the junction.
            var pts = new[] { c - dir * r * 0.6f + right * r, c - dir * r * 0.6f - right * r, c + dir * r * 0.9f };
            DrawColoredPolygon(pts, SignEdge);
            DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[0] }, StopSign, Legible(0.7f, 2f));
        }

        private void DrawCompass(RoadNetwork net)
        {
            float cx = 0, cy = 0; int k = 0;
            foreach (var n in net.Nodes) if (!n.IsBoundary) { cx += n.X; cy += n.Y; k++; }
            if (k == 0) return;
            cx /= k; cy /= k;
            int fontSize = Mathf.RoundToInt(Legible(7f * PixelsPerMeter, 16f));
            foreach (var n in net.Nodes)
            {
                if (!n.IsBoundary) continue;
                float dx = n.X - cx, dy = n.Y - cy;
                string label = Mathf.Abs(dx) > Mathf.Abs(dy) ? (dx > 0 ? "E" : "W") : (dy > 0 ? "N" : "S");
                var p = ToWorld(n.X, n.Y);
                var outward = (p - ToWorld(cx, cy)).Normalized();
                var at = p + outward * Legible(9f * PixelsPerMeter, 22f) + new Vector2(0f, fontSize * 0.35f);
                DrawString(Orbitope.MonoBold, at, label, HorizontalAlignment.Center, -1f, fontSize, Orbitope.TextSecondary);
            }
        }

        private static bool MovementServedFrom(Signal.Core.Node node, SignalController ctl, int inLink)
        {
            foreach (var m in node.Movements)
                if (m.InLink == inLink && ctl.MovementServed(m.Index)) return true;
            return false;
        }

        // ------------------------------------------------------------ picking

        /// <summary>Nearest editable junction within a generous radius.</summary>
        public bool TryPickNode(Vector2 worldPos, out int nodeId)
        {
            nodeId = -1;
            float best = Legible(14f * PixelsPerMeter, 28f);
            foreach (var node in Runner.Sim.Network.Nodes)
            {
                if (!IsEditable(node)) continue;
                float d = worldPos.DistanceTo(ToWorld(node.X, node.Y));
                if (d < best) { best = d; nodeId = node.Id; }
            }
            return nodeId >= 0;
        }

        /// <summary>Nearest approach (a link into an editable junction).</summary>
        public bool TryPickLink(Vector2 worldPos, out int linkId)
        {
            linkId = -1;
            float best = Legible(5f * PixelsPerMeter, 12f);
            var net = Runner.Sim.Network;
            foreach (var link in net.Links)
            {
                if (!IsEditable(net.NodeById(link.To))) continue;
                var (a, b) = LinkLine(link);
                float d = DistToSegment(worldPos, a, b);
                if (d < best) { best = d; linkId = link.Id; }
            }
            return linkId >= 0;
        }

        /// <summary>Hit test a click to the nearest signalized approach (tap zone
        /// = last 30 m of an in-link). Returns false if nothing close enough.</summary>
        public bool TryPickApproach(Vector2 worldPos, out int nodeId, out int inLinkId)
        {
            nodeId = -1; inLinkId = -1;
            float best = Legible(18f * PixelsPerMeter, 16f);
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
