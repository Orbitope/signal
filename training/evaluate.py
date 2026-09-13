"""
evaluate.py — score a trained policy against same-harness baselines.

    python training/evaluate.py training/runs/shared-fourway-bays-s0.pt

Rolls the trained policy and three reference controllers through the *same*
levels, seeds, and metric, so the comparison is apples-to-apples:

  trained  argmax of the learned masked policy
  fixed    round-robin over the legal phases (a fixed-time cycle)
  random   uniform over legal phases
  hold     never switch (phase 0 forever) — a floor

Metric is the pressure return the agents optimize, plus two things the plan
insists on reading together: mean through-queue and the WORST single-approach
head-wait (seconds) — a policy that wins on average by starving one approach
must be caught, and average wait alone will not catch it.

For the classical MaxPressure / fixed-time numbers in a different (C#) harness,
see `Signal.Headless bench`; those use link-level wait, not this obs proxy.
"""
from __future__ import annotations

import argparse
import sys

import numpy as np
import torch

from policies import REGISTRY
from signal_env import SignalVecEnv

QUEUE_NORM, WAIT_NORM = 20.0, 120.0

# obs indices, derived from the schema the env reports (works for v2 and v3).
IX = dict(thr_q=[2, 8, 14, 20], thr_w=[3, 9, 15, 21], bay_w=[1, 7, 13, 19],
          cur_phase=slice(24, 32), self=33)


def schema_indices(env):
    """Compute per-approach metric indices from the env's reported layout.
    Cardinal (N,E,S,W) slots are 0,2,4,6 under the 8-octant v3 schema, 0..3 under v2."""
    af = getattr(env, "approach_floats", 6)
    ma = getattr(env, "max_approaches", 4)
    sf = getattr(env, "self_floats", 33)
    card = [0, 2, 4, 6] if ma >= 8 else [0, 1, 2, 3]
    IX["bay_w"] = [s * af + 1 for s in card]
    IX["thr_q"] = [s * af + 2 for s in card]
    IX["thr_w"] = [s * af + 3 for s in card]
    IX["cur_phase"] = slice(ma * af, ma * af + 8)   # phase one-hot
    IX["self"] = sf
    return IX


def rollout(env, act_fn, episodes, warmup=1):
    """Run whole episodes; aggregate metrics over post-warmup decision steps."""
    obs, mask = env.reset()
    ep_done = 0
    ret = np.zeros((env.n_envs, env.n_agents))
    ep_returns, q_means, w_means, w_maxes, starved = [], [], [], [], []
    steps = 0
    while ep_done < episodes:
        a = act_fn(obs, mask)
        obs, rew, mask, done = env.step(a)
        ret += rew
        steps += 1
        # queue / wait proxies from the fresh obs (skip the reset frame)
        if not done.any():
            q = obs[..., IX["thr_q"]]
            q_means.append(float(q[q >= 0.0].mean()) * QUEUE_NORM if (q >= 0).any() else 0.0)
            w = np.concatenate([obs[..., IX["thr_w"]], obs[..., IX["bay_w"]]], axis=-1)
            wv = w[w >= 0.0]   # exclude missing-approach sentinel (-1); one-ways have many
            w_means.append(float(wv.mean()) * WAIT_NORM if wv.size else 0.0)
            w_maxes.append(float(w.max()) * WAIT_NORM)
            # Starvation proxy: fraction of approach through-waits pinned at the
            # clamp (>=0.99). Unlike max_wait it does not saturate to a constant,
            # so it catches a policy that wins on average by starving one approach.
            tw = obs[..., IX["thr_w"]]
            starved.append(float((tw >= 0.99).mean()))
        for e in range(env.n_envs):
            if done[e]:
                ep_returns.extend(ret[e].tolist())
                ret[e] = 0.0
                ep_done += 1
    return dict(ep_return=float(np.mean(ep_returns)),
                mean_queue=float(np.mean(q_means)),
                mean_wait=float(np.mean(w_means)) if w_means else float("nan"),
                starved_frac=float(np.mean(starved)) if starved else float("nan"))


def make_baselines(rng):
    def random_legal(obs, mask):
        p = mask.astype(np.float64)
        p /= p.sum(-1, keepdims=True)
        c = p.cumsum(-1)
        u = rng.random(c.shape[:-1] + (1,))
        return (u < c).argmax(-1)

    def hold(obs, mask):
        return np.zeros(obs.shape[:2], dtype=int)

    def fixed_cycle(obs, mask):
        # request the next legal phase after the current one, wrapping — a
        # clean round robin because illegal high phases are already masked.
        cur = obs[..., IX["cur_phase"]].argmax(-1)    # (E,A)
        out = cur.copy()
        E, A, P = mask.shape
        for e in range(E):
            for a in range(A):
                legal = np.nonzero(mask[e, a])[0]
                nxt = legal[legal > cur[e, a]]
                out[e, a] = nxt[0] if len(nxt) else legal[0]
        return out
    return {"random": random_legal, "hold": hold, "fixed": fixed_cycle}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ckpt")
    ap.add_argument("--project", default="Signal.EnvServer")
    ap.add_argument("--envs", type=int, default=8)
    ap.add_argument("--episodes", type=int, default=24)
    ap.add_argument("--seed", type=int, default=1000)
    ap.add_argument("--level", default=None, help="override eval level")
    ap.add_argument("--demand", type=float, default=None,
                    help="override demand multiplier (stress test)")
    args = ap.parse_args()

    meta = torch.load(args.ckpt, map_location="cpu", weights_only=False)
    level = args.level or meta["level"]
    policy = REGISTRY[meta["rung"]](
        meta["obs_size"], meta["n_actions"], meta["n_agents"],
        hidden=(meta["hidden"], meta["hidden"]), **meta.get("schema", {}))
    policy.load_state_dict(meta["state_dict"])
    policy.eval()
    blind = meta.get("blind_neighbours", False)

    dlo = args.demand if args.demand is not None else meta["args"]["demand_lo"]
    dhi = args.demand if args.demand is not None else meta["args"]["demand_hi"]

    def env_of(seed):
        return SignalVecEnv(args.project, level=level, n_envs=args.envs, seed=seed,
                            decision_ticks=meta["args"]["decision_ticks"],
                            episode_steps=meta["args"]["episode_steps"],
                            demand=(dlo, dhi))

    _probe = env_of(args.seed); _probe.reset(); schema_indices(_probe); _probe.close()

    def trained(obs, mask):
        o = torch.as_tensor(np.array(obs), dtype=torch.float32)
        if blind:
            o = o.clone(); o[..., IX["self"]:] = 0.0
        return policy.act_greedy(o, torch.as_tensor(np.array(mask), dtype=torch.float32)).numpy()

    rng = np.random.default_rng(0)
    conditions = {"trained": trained, **make_baselines(rng)}

    print(f"== eval {meta['rung']} on {level} "
          f"({args.episodes} episodes x {args.envs} envs, seed {args.seed}) ==")
    print(f"   {'policy':<10} {'ep_return':>10} {'mean_queue':>11} "
          f"{'mean_wait_s':>12} {'starved%':>9}")
    rows = {}
    for name, fn in conditions.items():
        env = env_of(args.seed)
        try:
            m = rollout(env, fn, args.episodes)
        finally:
            env.close()
        rows[name] = m
        print(f"   {name:<10} {m['ep_return']:>10.3f} {m['mean_queue']:>11.2f} "
              f"{m['mean_wait']:>12.1f} {100*m['starved_frac']:>8.1f}%")

    t, f = rows["trained"]["ep_return"], rows["fixed"]["ep_return"]
    print(f"\n   trained vs fixed-cycle return: {t:.3f} vs {f:.3f}  "
          f"({'+' if t > f else ''}{t - f:.3f}, "
          f"{'trained wins' if t > f else 'fixed wins'})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
