"""
signal_env.py — Python client for Signal.EnvServer.

SignalVecEnv speaks the stdio protocol (4-byte LE length-prefixed frames;
JSON control out, JSON header + raw float32/uint8 tensor block back) and
exposes the SB3 VecEnv-ish surface: reset() -> obs, step(actions) ->
(obs, rewards, dones, masks). Shapes:
    obs     (n_envs, n_agents, 105) float32
    rewards (n_envs, n_agents)      float32
    masks   (n_envs, n_agents, 8)   uint8   (1 = legal)
    dones   (n_envs,)               bool

Run this file directly for the validation suite (round-trip, shapes,
determinism, masks, gate folding, throughput).
"""
import json
import struct
import subprocess
import sys
import time

import numpy as np

SERVER_CMD = ["dotnet", "run", "-c", "Release", "--no-build", "--project"]


class SignalVecEnv:
    def __init__(self, server_project, level="fourway-bays", n_envs=8, seed=1,
                 decision_ticks=50, episode_steps=120, demand=(1.0, 1.0)):
        self.proc = subprocess.Popen(
            SERVER_CMD + [server_project],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        self._cfg = dict(cmd="reset", level=level, n_envs=n_envs, seed=seed,
                         decision_ticks=decision_ticks, episode_steps=episode_steps,
                         demand_lo=demand[0], demand_hi=demand[1])
        self.n_envs = n_envs
        self.n_agents = None
        self.obs_size = None
        self.n_actions = None

    # -- framing ------------------------------------------------------------
    def _send(self, obj):
        payload = json.dumps(obj).encode()
        self.proc.stdin.write(struct.pack("<I", len(payload)) + payload)
        self.proc.stdin.flush()

    def _read_frame(self):
        hdr = self.proc.stdout.read(4)
        if len(hdr) < 4:
            raise EOFError("server closed")
        (n,) = struct.unpack("<I", hdr)
        buf = self.proc.stdout.read(n)
        assert len(buf) == n
        return buf

    def _recv_state(self):
        h = json.loads(self._read_frame())
        self.n_agents = h["n_agents"]
        self.obs_size = h["obs_size"]
        self.n_actions = h["n_actions"]
        # schema layout (schema v3 sends these; default to the v2 4-approach layout)
        self.max_approaches = h.get("max_approaches", 4)
        self.approach_floats = h.get("approach_floats", 6)
        self.self_floats = h.get("self_floats", 33)
        self.neighbor_floats = h.get("neighbor_floats", 18)
        blob = self._read_frame()
        o = h["obs_bytes"]; r = h["rew_bytes"]; m = h["mask_bytes"]
        obs = np.frombuffer(blob, np.float32, count=o // 4).reshape(
            self.n_envs, self.n_agents, self.obs_size)
        rew = np.frombuffer(blob, np.float32, count=r // 4, offset=o).reshape(
            self.n_envs, self.n_agents)
        mask = np.frombuffer(blob, np.uint8, count=m, offset=o + r).reshape(
            self.n_envs, self.n_agents, self.n_actions)
        done = np.frombuffer(blob, np.uint8, count=h["done_bytes"],
                             offset=o + r + m).astype(bool)
        return obs, rew, mask, done

    # -- api ----------------------------------------------------------------
    def reset(self):
        self._send(self._cfg)
        obs, _, mask, _ = self._recv_state()
        return obs, mask

    def step(self, actions):
        self._send({"cmd": "step", "actions": np.asarray(actions).tolist()})
        return self._recv_state()

    def close(self):
        try:
            self._send({"cmd": "close"})
        except Exception:
            pass
        self.proc.wait(timeout=10)


# ===========================================================================
#  Validation suite
# ===========================================================================
def main():
    project = sys.argv[1] if len(sys.argv) > 1 else "Signal.EnvServer"
    failures = 0

    def check(ok, name, detail=""):
        nonlocal failures
        print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f": {detail}" if detail and not ok else ""))
        if not ok:
            failures += 1

    rng = np.random.default_rng(0)

    def masked_random(mask):
        # random legal action per agent
        p = mask.astype(np.float64)
        p /= p.sum(-1, keepdims=True)
        cum = p.cumsum(-1)
        u = rng.random(cum.shape[:-1] + (1,))
        return (u < cum).argmax(-1)

    print("== shapes & round trip (fourway-bays, 4 envs) ==")
    env = SignalVecEnv(project, level="fourway-bays", n_envs=4, seed=7)
    obs, mask = env.reset()
    O = env.obs_size
    af, ma, sf, nf = env.approach_floats, env.max_approaches, env.self_floats, env.neighbor_floats
    card = [0, 2, 4, 6] if ma >= 8 else [0, 1, 2, 3]     # N,E,S,W slots
    hasbay_ix = [s * af + 4 for s in card]
    thrq_ix = [s * af + 2 for s in card]
    bayw_ix = [s * af + 1 for s in card]
    nbvalid_ix = list(range(sf + nf - 1, O, nf))          # 'valid' flag of each neighbour block
    check(obs.shape == (4, 1, O), f"obs shape (4,1,{O})", str(obs.shape))
    check(mask.shape == (4, 1, 8), "mask shape (4,1,8)", str(mask.shape))
    check(np.isfinite(obs).all(), "obs finite")
    check(obs.min() >= -1.0001 and obs.max() <= 2.0001, "obs in range", f"[{obs.min()},{obs.max()}]")
    # 4-phase protected lefts -> phases 4..7 illegal always
    check((mask[..., 4:] == 0).all(), "phases beyond count masked")
    check((mask[..., 0] == 1).all(), "current phase always legal")
    # bays present -> hasBay flag set on all four cardinal approaches
    has_bay = obs[0, 0, hasbay_ix]
    check((has_bay == 1).all(), "hasBay flags set", str(has_bay))

    print("== min-green masking dynamics ==")
    obs, rew, mask, done = env.step(np.zeros((4, 1), int))
    # after 5 sim-sec (= min-green), switching should be legal
    check((mask[..., :4] == 1).all(), "all real phases legal after min-green")
    env.close()

    print("== determinism across server instances ==")
    a = SignalVecEnv(project, level="fourway-bays", n_envs=2, seed=42)
    b = SignalVecEnv(project, level="fourway-bays", n_envs=2, seed=42)
    oa, _ = a.reset(); ob, _ = b.reset()
    check(np.array_equal(oa, ob), "reset obs identical")
    ident = True
    for t in range(20):
        acts = masked_random(np.ones((2, 1, 4), np.uint8))
        ra = a.step(acts); rb = b.step(acts)
        if not all(np.array_equal(x, y) for x, y in zip(ra[:3], rb[:3])):
            ident = False
            break
    check(ident, "20-step trajectories identical under same actions")
    c = SignalVecEnv(project, level="fourway-bays", n_envs=2, seed=43)
    c.reset()
    rc = None
    for t in range(5):   # t=0 obs is an empty network; divergence needs traffic
        acts = np.zeros((2, 1), int)
        ra = a.step(acts); rc = c.step(acts)
    check(not np.array_equal(ra[0], rc[0]), "different seed diverges after steps")
    a.close(); b.close(); c.close()

    print("== rewards & gate folding under load ==")
    env = SignalVecEnv(project, level="fourway-bays", n_envs=2, seed=5,
                       demand=(1.6, 1.6), episode_steps=40)
    obs, mask = env.reset()
    total = 0.0
    thr_q_max = 0.0
    bay_wait = 0.0
    saw_done = False
    for t in range(40):
        obs, rew, mask, done = env.step(np.zeros((2, 1), int))  # hold phase 0: starve lefts
        total += rew.sum()
        saw_done = saw_done or bool(done.any())
        if not done.any():   # post-reset obs belongs to a fresh episode
            thr_q_max = max(thr_q_max, float(obs[..., thrq_ix].max()))
            bay_wait = max(bay_wait, float(obs[..., bayw_ix].max()))
    check(total < 0, "pressure reward negative under congestion", f"{total:.2f}")
    check(thr_q_max >= 0.99, "queues saturate obs (incl. arm + gate folding)", f"{thr_q_max:.2f}")
    check(saw_done, "episode auto-reset fired (episode_steps=40)")
    check(bay_wait > 0.3, "starved bay head-wait visible in obs", f"{bay_wait:.2f}")
    env.close()

    print("== multi-agent grid (grid2: 4 agents) ==")
    env = SignalVecEnv(project, level="grid2", n_envs=2, seed=3)
    obs, mask = env.reset()
    check(obs.shape == (2, 4, O), f"obs shape (2,4,{O})", str(obs.shape))
    # interior nodes have signalized neighbors -> neighbor valid flags present
    nb_valid = obs[0, 0, nbvalid_ix]
    check(nb_valid.sum() >= 2, "neighbors visible", str(nb_valid))
    obs, rew, mask, done = env.step(np.zeros((2, 4), int))
    check(rew.shape == (2, 4), "per-agent rewards")
    env.close()

    print("== throughput (8 envs, fourway-bays) ==")
    env = SignalVecEnv(project, level="fourway-bays", n_envs=8, seed=1)
    obs, mask = env.reset()
    t0 = time.time()
    steps = 200
    for _ in range(steps):
        obs, rew, mask, done = env.step(masked_random(mask[..., :4]))
    dt = time.time() - t0
    tuples = steps * 8 * 1
    print(f"        {steps} decision-steps x 8 envs in {dt:.2f}s "
          f"= {tuples/dt:,.0f} tuples/sec ({steps*8*50/dt:,.0f} sim-steps/sec through the pipe)")
    check(tuples / dt > 300, "pipeline throughput adequate", f"{tuples/dt:.0f}/s")
    env.close()

    print("\n" + ("ALL ENV TESTS PASSED" if failures == 0 else f"{failures} FAILURES"))
    sys.exit(failures)


if __name__ == "__main__":
    main()
