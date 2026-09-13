"""
summarize.py — aggregate greedy-eval metrics across seeds into mean +/- std.

    python training/summarize.py training/runs/shared-corridor-sat-s*.pt \
                                 training/runs/independent-corridor-sat-s*.pt

Groups checkpoints by tag with the trailing -s<seed> stripped, evaluates each
(trained greedy) on its own level/demand, and reports mean +/- std per group so
the coordination gap can be read against seed noise — the plan's >=3-seed bar.
Also evaluates the fixed-cycle baseline once per (level, demand) for reference.
"""
from __future__ import annotations

import argparse
import re
import statistics as st

import numpy as np
import torch

from evaluate import rollout, make_baselines, schema_indices, IX
from policies import REGISTRY
from signal_env import SignalVecEnv

PROJ_DEFAULT = "Signal.EnvServer"


def trained_fn(policy, blind):
    def fn(obs, mask):
        o = torch.as_tensor(np.array(obs), dtype=torch.float32)
        if blind:
            o = o.clone(); o[..., IX["self"]:] = 0.0
        return policy.act_greedy(o, torch.as_tensor(np.array(mask),
                                                    dtype=torch.float32)).numpy()
    return fn


def group_of(tag):
    return re.sub(r"-s\d+$", "", tag)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ckpts", nargs="+")
    ap.add_argument("--project", default=PROJ_DEFAULT)
    ap.add_argument("--envs", type=int, default=6)
    ap.add_argument("--episodes", type=int, default=18)
    ap.add_argument("--seed", type=int, default=1000)
    args = ap.parse_args()

    groups: dict[str, list] = {}
    baselines_done: dict[tuple, dict] = {}
    metrics = ("ep_return", "mean_queue", "mean_wait", "starved_frac")

    for ckpt in args.ckpts:
        meta = torch.load(ckpt, map_location="cpu", weights_only=False)
        tag = meta["args"]["tag"]
        level = meta["level"]
        dlo = meta["args"]["demand_lo"]; dhi = meta["args"]["demand_hi"]
        pol = REGISTRY[meta["rung"]](meta["obs_size"], meta["n_actions"],
                                     meta["n_agents"],
                                     hidden=(meta["hidden"], meta["hidden"]),
                                     **meta.get("schema", {}))
        pol.load_state_dict(meta["state_dict"]); pol.eval()
        blind = meta.get("blind_neighbours", False)

        def env_of():
            return SignalVecEnv(args.project, level=level, n_envs=args.envs,
                                seed=args.seed,
                                decision_ticks=meta["args"]["decision_ticks"],
                                episode_steps=meta["args"]["episode_steps"],
                                demand=(dlo, dhi))

        env = env_of()
        try:
            env.reset(); schema_indices(env)
            m = rollout(env, trained_fn(pol, blind), args.episodes)
        finally:
            env.close()
        groups.setdefault(group_of(tag), []).append(m)
        print(f"  {tag:32s} ret {m['ep_return']:8.1f}  wait {m['mean_wait']:5.1f}  "
              f"starved {100*m['starved_frac']:4.1f}%")

        key = (level, dlo, dhi)
        if key not in baselines_done:
            env = env_of()
            try:
                fx = rollout(env, make_baselines(np.random.default_rng(0))["fixed"],
                             args.episodes)
            finally:
                env.close()
            baselines_done[key] = fx

    print("\n== per-group mean +/- std (greedy) ==")
    print(f"  {'group':<30} {'n':>2}  {'ep_return':>16}  {'mean_wait_s':>14}  "
          f"{'starved%':>10}")
    for g, ms in sorted(groups.items()):
        def agg(k):
            vals = [m[k] for m in ms]
            return st.mean(vals), (st.pstdev(vals) if len(vals) > 1 else 0.0)
        rmu, rsd = agg("ep_return"); wmu, wsd = agg("mean_wait")
        smu, ssd = agg("starved_frac")
        print(f"  {g:<30} {len(ms):>2}  {rmu:8.1f} +/-{rsd:5.1f}  "
              f"{wmu:6.1f} +/-{wsd:4.1f}  {100*smu:5.1f} +/-{100*ssd:4.1f}")

    print("\n== fixed-cycle reference ==")
    for (level, dlo, dhi), fx in baselines_done.items():
        print(f"  {level} @{dlo}-{dhi}x: ret {fx['ep_return']:8.1f}  "
              f"wait {fx['mean_wait']:5.1f}  starved {100*fx['starved_frac']:4.1f}%")


if __name__ == "__main__":
    main()
