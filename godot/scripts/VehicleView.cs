using Godot;
using Signal.Core;

namespace SignalGodot
{
    /// <summary>
    /// All vehicles in one draw call via MultiMesh. Per-instance transform from
    /// the interpolated sim state; per-instance color encodes wait stress
    /// (calm steel -> hot amber as a vehicle's wait climbs). Cars are drawn at
    /// real size when zoomed in and scaled up to a minimum on-screen length
    /// when zoomed out, so a city-scale view still shows traffic, not dust.
    /// </summary>
    public partial class VehicleView : MultiMeshInstance2D
    {
        public SimRunner Runner;
        public NetworkView Net;

        [Export] public float MinScreenLength = 20f;   // never draw a car shorter than this on screen

        // Steel = calm flow; ramp to Amber, then AmberBright, as wait climbs.
        // Never coral (reserved for spillback).
        private static readonly Color Calm = Orbitope.SteelBright;
        private static readonly Color Warm = Orbitope.Amber;
        private static readonly Color Hot = Orbitope.AmberBright;
        private const int MaxInstances = 4096;
        private const float CarLength = 10f, CarWidth = 5.5f;   // world px (~4.5 x 2.5 m at 2.2 px/m)

        public override void _Ready()
        {
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
                UseColors = true,
                Mesh = new QuadMesh { Size = new Vector2(CarLength, CarWidth) },
            };
            mm.InstanceCount = MaxInstances;   // set AFTER formats: Godot locks them once instances exist
            Multimesh = mm;
            for (int i = 0; i < MaxInstances; i++) HideInstance(i);
        }

        public override void _Process(double delta)
        {
            if (Runner?.Sim == null || Net == null) return;
            var net = Runner.Sim.Network;

            // Zoom-aware size: scale up only when the real size would be too small.
            float s = Mathf.Max(1f, MinScreenLength / (Mathf.Max(Net.Zoom, 0.01f) * CarLength));
            var scale = new Vector2(s, s);

            int i = 0;
            foreach (var (id, linkId, pos) in Runner.InterpolatedVehicles())
            {
                if (i >= MaxInstances) break;
                var link = net.LinkById(linkId);
                var (world, dir) = Net.Pose(link, pos);

                Multimesh.SetInstanceTransform2D(i, new Transform2D(dir.Angle(), scale, 0f, world));

                // Color by wait stress. Vehicle lookup by id would be a dictionary
                // hit per vehicle per frame; instead ride along the link's list.
                float wait = FindWait(link, id);
                float t = Mathf.Clamp(wait / 60f, 0f, 1f);
                var col = t < 0.5f ? Calm.Lerp(Warm, t * 2f) : Warm.Lerp(Hot, (t - 0.5f) * 2f);
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
