using Godot;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// All vehicles in one draw call via MultiMesh. Per-instance transform from
    /// the interpolated sim state; per-instance color encodes wait stress
    /// (calm slate -> hot coral as a vehicle's wait climbs).
    /// </summary>
    public partial class VehicleView : MultiMeshInstance2D
    {
        public SimRunner Runner;
        public NetworkView Net;

        // Steel = calm flow; ramp to Amber, then AmberBright, as wait climbs.
        // Never coral (reserved for spillback).
        private static readonly Color Calm = Orbitope.Steel;
        private static readonly Color Warm = Orbitope.Amber;
        private static readonly Color Hot = Orbitope.AmberBright;
        private const int MaxInstances = 4096;

        public override void _Ready()
        {
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
                UseColors = true,
                Mesh = new QuadMesh { Size = new Vector2(9f, 5f) },
            };
            mm.InstanceCount = MaxInstances;   // set AFTER formats: Godot locks them once instances exist
            Multimesh = mm;
            for (int i = 0; i < MaxInstances; i++) HideInstance(i);
        }

        public override void _Process(double delta)
        {
            if (Runner?.Sim == null || Net == null) return;
            var net = Runner.Sim.Network;
            int i = 0;

            foreach (var (id, linkId, pos) in Runner.InterpolatedVehicles())
            {
                if (i >= MaxInstances) break;
                var link = net.LinkById(linkId);
                var (a, b) = Net.LinkLine(link);
                var dir = (b - a).Normalized();
                var world = a.Lerp(b, Mathf.Clamp(pos / link.Length, 0f, 1f));

                var xf = new Transform2D(dir.Angle(), world);
                Multimesh.SetInstanceTransform2D(i, xf);

                // Color by wait stress. Vehicle lookup by id would be a dictionary
                // hit per vehicle per frame; instead ride along the link's list.
                float wait = FindWait(link, id);
                float s = Mathf.Clamp(wait / 60f, 0f, 1f);
                var col = s < 0.5f ? Calm.Lerp(Warm, s * 2f) : Warm.Lerp(Hot, (s - 0.5f) * 2f);
                Multimesh.SetInstanceColor(i, col);
                i++;
            }
            for (; i < MaxInstances; i++) HideInstance(i);
        }

        private static float FindWait(Link link, long id)
        {
            for (int v = 0; v < link.Vehicles.Count; v++)
                if (link.Vehicles[v].Id == id) return link.Vehicles[v].Wait;
            return 0f;
        }

        private void HideInstance(int i)
            => Multimesh.SetInstanceTransform2D(i, new Transform2D(0f, new Vector2(1e6f, 1e6f)));
    }
}
