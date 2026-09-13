"""
record_sim.py — record real rollouts for the live-sim article figure.

Reuses evaluate.py's exact action functions (trained argmax / fixed-time cycle /
random-legal) so the recorded trajectories carry the same metrics the article
reports. Drives the env server in trace mode (env 0 emits per-tick render
frames), records ONE episode per controller on the same seed, and writes a
compact, base64-packed JS data file the browser widget replays.

    python training/record_sim.py training/runs/shared-sc-couplet-satv3-s0.pt \
        --out docs/sim_data.js --seconds 70 --frame-every 3

Static geometry comes from `--dump`, never from the trace stream.
"""
from __future__ import annotations

import argparse
import base64
import json
import struct
import subprocess
import sys

import numpy as np
import torch

from policies import REGISTRY
import evaluate as ev

# Debug build: separate output dir so we never fight the Release DLL a
# concurrent training run holds open via `dotnet run -c Release --no-build`.
DLL = "Signal.EnvServer/bin/Debug/net8.0/Signal.EnvServer.dll"


class TraceEnv:
    """Single-env client that also reads the trailing trace frame."""

    def __init__(self, level, seed, decision_ticks, episode_steps, demand, frame_every):
        self.proc = subprocess.Popen(["dotnet", DLL],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        self.cfg = dict(cmd="reset", level=level, n_envs=1, seed=seed,
                        decision_ticks=decision_ticks, episode_steps=episode_steps,
                        demand_lo=demand, demand_hi=demand, trace=True,
                        frame_every=frame_every)
        self.n_agents = self.obs_size = self.n_actions = None
        self.max_approaches = 4; self.approach_floats = 6
        self.self_floats = 33; self.neighbor_floats = 18

    def _send(self, obj):
        b = json.dumps(obj).encode()
        self.proc.stdin.write(struct.pack("<I", len(b)) + b); self.proc.stdin.flush()

    def _frame(self):
        hdr = self.proc.stdout.read(4)
        if len(hdr) < 4:
            raise EOFError("server closed")
        (n,) = struct.unpack("<I", hdr)
        buf = b""
        while len(buf) < n:
            chunk = self.proc.stdout.read(n - len(buf))
            if not chunk:
                raise EOFError("short read")
            buf += chunk
        return buf

    def _recv_state(self):
        h = json.loads(self._frame())
        self.n_agents = h["n_agents"]; self.obs_size = h["obs_size"]
        self.n_actions = h["n_actions"]
        self.max_approaches = h.get("max_approaches", 4)
        self.approach_floats = h.get("approach_floats", 6)
        self.self_floats = h.get("self_floats", 33)
        self.neighbor_floats = h.get("neighbor_floats", 18)
        blob = self._frame()
        o, r, m = h["obs_bytes"], h["rew_bytes"], h["mask_bytes"]
        obs = np.frombuffer(blob, np.float32, o // 4).reshape(1, self.n_agents, self.obs_size)
        rew = np.frombuffer(blob, np.float32, r // 4, o).reshape(1, self.n_agents)
        mask = np.frombuffer(blob, np.uint8, m, o + r).reshape(1, self.n_agents, self.n_actions)
        done = np.frombuffer(blob, np.uint8, h["done_bytes"], o + r + m).astype(bool)
        return obs, rew, mask, done

    def reset(self):
        self._send(self.cfg)
        obs, _, mask, _ = self._recv_state()
        self.meta = json.loads(self._frame())      # trace_meta frame
        return obs, mask

    def step(self, actions):
        self._send({"cmd": "step", "actions": np.asarray(actions).tolist()})
        obs, rew, mask, done = self._recv_state()
        tr = json.loads(self._frame())             # trace frame
        return obs, rew, mask, done, tr

    def close(self):
        try:
            self._send({"cmd": "close"}); self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


def b64(arr):
    return base64.b64encode(arr.tobytes()).decode()


def record_controller(level, seed, dt_ticks, ep_steps, demand, frame_every,
                      act_fn, n_decisions, n_warm=0, gl_bits=0):
    """Run one episode; return packed frame arrays.

    n_warm decisions are stepped WITHOUT capture, so the recorded window opens
    on an already-loaded network at (dis)equilibrium — the trained/fixed gap is
    visible from the first frame instead of buried under the empty-network fill.
    Metrics stay cumulative from t=0, so the running averages already reflect it."""
    env = TraceEnv(level, seed, dt_ticks, ep_steps, demand, frame_every)
    obs, mask = env.reset()
    node_ids = env.meta["node_ids"]
    n_nodes = len(node_ids)

    for _ in range(n_warm):
        obs, rew, mask, done, tr = env.step(act_fn(obs, mask))

    gl_bytes = (gl_bits + 7) // 8       # bytes per frame for the green-link bitmask
    link_ids, pos, stop, nv, sig, met, glraw = [], [], [], [], [], [], []
    for _ in range(n_decisions):
        a = act_fn(obs, mask)
        obs, rew, mask, done, tr = env.step(a)
        for fr in tr["frames"]:
            v = fr["v"]; qf = fr["q"]
            k = len(v) // 2
            nv.append(k)
            for i in range(k):
                link_ids.append(v[2 * i])
                pos.append(int(round(v[2 * i + 1] * 255 / 1000)))   # 0..1000 -> 0..255
                stop.append(qf[i])
            sig.extend(int(x) for x in fr["s"])                     # state per node
            if gl_bits:                                             # green approach-links
                flags = np.zeros(gl_bits, np.uint8)
                for lid in fr.get("gl", ()):
                    if lid < gl_bits:
                        flags[lid] = 1
                glraw.append(np.packbits(flags))
            m = fr["m"]
            met.extend([m["cl"], m["tp"], m["aw"], m["insys"], m["sb"], m["qd"]])
        if done[0]:
            break
    env.close()

    stopbits = np.packbits(np.array(stop, np.uint8)) if stop else np.array([], np.uint8)
    glcat = np.concatenate(glraw) if glraw else np.array([], np.uint8)
    return dict(
        nframes=len(nv), n_nodes=n_nodes, n_veh=len(pos),
        nv=b64(np.array(nv, np.uint16)),
        link=b64(np.array(link_ids, np.uint16)),
        pos=b64(np.array(pos, np.uint8)),
        stop=b64(stopbits),
        sig=b64(np.array(sig, np.uint8)),
        green=b64(glcat), gl_bytes=gl_bytes,
        met=b64(np.array(met, np.float32)),
    )


def geometry(level):
    js = subprocess.check_output(["dotnet", DLL, "--dump", level]).decode()
    d = json.loads(js)
    nodes = {n["id"]: n for n in d["network"]["nodes"]}
    links = d["network"]["links"]
    pairset = {(l["from"], l["to"]) for l in links}
    gnodes = [dict(id=n["id"], x=n["x"], y=n["y"],
                   bnd=bool(n.get("isBoundary")),
                   sig=(n.get("control", 0) == 1))
              for n in nodes.values()]
    glinks = []
    for l in links:
        a, b = nodes[l["from"]], nodes[l["to"]]
        glinks.append(dict(
            id=l["id"], f=l["from"], t=l["to"], len=l["length"],
            ax=a["x"], ay=a["y"], bx=b["x"], by=b["y"],
            ow=((l["to"], l["from"]) not in pairset),
            fast=(l.get("speedLimit", 13.9) > 14.0),
            right=(l.get("turns") == 4),
            bnd=bool(a.get("isBoundary") or b.get("isBoundary")),
        ))
    return dict(nodes=gnodes, links=glinks)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ckpt")
    ap.add_argument("--out", default="docs/sim_data.js")
    ap.add_argument("--seed", type=int, default=1234)
    ap.add_argument("--seconds", type=float, default=70.0)
    ap.add_argument("--warmup", type=float, default=0.0,
                    help="seconds to run (per controller) before capture, to skip the empty-network transient")
    ap.add_argument("--frame-every", type=int, default=3)
    ap.add_argument("--demand", type=float, default=None,
                    help="override demand multiplier (default: the checkpoint's)")
    ap.add_argument("--level", default=None, help="override level (needs matching policy unless --baselines-only)")
    ap.add_argument("--baselines-only", action="store_true",
                    help="record only fixed/random (no trained policy) — for capacity probes")
    args = ap.parse_args()

    meta = torch.load(args.ckpt, map_location="cpu", weights_only=False)
    level = args.level or meta["level"]
    dt_ticks = meta["args"]["decision_ticks"]
    demand = args.demand if args.demand is not None else meta["args"]["demand_hi"]
    ep_steps = meta["args"]["episode_steps"]
    secs_per_dec = dt_ticks * 0.1
    n_warm = int(round(args.warmup / secs_per_dec))
    n_decisions = min(ep_steps - n_warm, int(round(args.seconds / secs_per_dec)))

    policy = REGISTRY[meta["rung"]](
        meta["obs_size"], meta["n_actions"], meta["n_agents"],
        hidden=(meta["hidden"], meta["hidden"]), **meta.get("schema", {}))
    policy.load_state_dict(meta["state_dict"]); policy.eval()
    blind = meta.get("blind_neighbours", False)

    # derive obs indices from a probe env (same as evaluate.py)
    probe = TraceEnv(level, args.seed, dt_ticks, ep_steps, demand, args.frame_every)
    probe.reset(); ev.schema_indices(probe)
    node_ids = probe.meta["node_ids"]     # signal node ids, in agent (=sig) order
    probe.close()

    def trained(obs, mask):
        o = torch.as_tensor(np.array(obs), dtype=torch.float32)
        if blind:
            o = o.clone(); o[..., ev.IX["self"]:] = 0.0
        return policy.act_greedy(o, torch.as_tensor(np.array(mask), dtype=torch.float32)).numpy()

    rng = np.random.default_rng(0)
    base = ev.make_baselines(rng)
    conditions = {"fixed": base["fixed"], "random": base["random"]}
    if not args.baselines_only:
        conditions = {"trained": trained, **conditions}

    print(f"recording {level}: {n_decisions} decisions x {dt_ticks} ticks, "
          f"frame_every={args.frame_every}, seed={args.seed}")
    geo = geometry(level)
    gl_bits = max(l["id"] for l in geo["links"]) + 1
    out = dict(level=level, seed=args.seed, dt=0.1, demand=demand,
               frame_every=args.frame_every, decision_ticks=dt_ticks,
               t0=args.warmup, rung=meta["rung"], node_ids=node_ids,
               geometry=geo, controllers={})
    for name, fn in conditions.items():
        rec = record_controller(level, args.seed, dt_ticks, ep_steps, demand,
                                args.frame_every, fn, n_decisions, n_warm, gl_bits)
        out["controllers"][name] = rec
        # final metrics for a quick sanity line
        met = np.frombuffer(base64.b64decode(rec["met"]), np.float32).reshape(-1, 6)
        print(f"  {name:8} {rec['nframes']} frames  "
              f"cleared={int(met[-1,0])} thru/min={met[-1,1]:.1f} "
              f"avg_wait={met[-1,2]:.1f}s peak_insys={int(met[:,3].max())}")

    with open(args.out, "w") as f:
        f.write("window.SIM_DATA = ")
        json.dump(out, f, separators=(",", ":"))
        f.write(";\n")
    import os
    print(f"wrote {args.out}  ({os.path.getsize(args.out)/1024:.0f} KB)")


if __name__ == "__main__":
    main()
