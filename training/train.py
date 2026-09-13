"""
train.py — train one rung of the Signal ladder.

    python training/train.py --rung shared      --level fourway-bays
    python training/train.py --rung independent --level grid2
    python training/train.py --rung central     --level grid2

Build order from the plan is rung 3 (shared) first: it is the deployment target,
the cheapest to train, and it forces the obs/plumbing everything else reuses.
Rung 1 (independent) is this same trainer with --rung independent, which turns
sharing off and blinds each brain to its neighbours. Rung 2 (central) adds the
centralized critic.

Writes <out>/<tag>.pt (weights + meta) and <out>/<tag>.json (the training log).
"""
from __future__ import annotations

import argparse
import json
import os

import torch

from policies import REGISTRY
from ppo import PPOConfig, train
from signal_env import SignalVecEnv


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rung", choices=list(REGISTRY), default="shared")
    ap.add_argument("--level", default="fourway-bays")
    ap.add_argument("--project", default="Signal.EnvServer")
    ap.add_argument("--envs", type=int, default=16)
    ap.add_argument("--steps", type=int, default=400_000, help="decision-tuples/agent")
    ap.add_argument("--rollout", type=int, default=128)
    ap.add_argument("--hidden", type=int, default=128)
    ap.add_argument("--decision-ticks", type=int, default=50)   # 5 sim-sec @ DT=0.1
    ap.add_argument("--episode-steps", type=int, default=120)   # 600 sim-sec
    ap.add_argument("--demand-lo", type=float, default=0.8)
    ap.add_argument("--demand-hi", type=float, default=1.2)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--ent-coef", type=float, default=0.01)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--out", default="training/runs")
    ap.add_argument("--tag", default=None)
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    tag = args.tag or f"{args.rung}-{args.level}-s{args.seed}"
    print(f"== training rung={args.rung} level={args.level} tag={tag} ==")

    env = SignalVecEnv(
        args.project, level=args.level, n_envs=args.envs, seed=args.seed + 1,
        decision_ticks=args.decision_ticks, episode_steps=args.episode_steps,
        demand=(args.demand_lo, args.demand_hi))
    obs, mask = env.reset()
    print(f"   n_agents={env.n_agents}  obs={env.obs_size}  actions={env.n_actions}")

    blind = args.rung == "independent"     # rung 1 sees only itself
    schema = dict(self_floats=env.self_floats, neighbor_floats=env.neighbor_floats,
                  max_neighbors=env.max_approaches)
    policy = REGISTRY[args.rung](
        env.obs_size, env.n_actions, env.n_agents, hidden=(args.hidden, args.hidden),
        **schema)
    n_params = sum(p.numel() for p in policy.parameters())
    print(f"   policy={policy.kind}  params={n_params:,}  blind_neighbours={blind}")

    cfg = PPOConfig(total_steps=args.steps, rollout=args.rollout, lr=args.lr,
                    ent_coef=args.ent_coef, seed=args.seed)

    try:
        log = train(env, policy, cfg, blind_neighbours=blind)
    finally:
        env.close()

    ckpt = os.path.join(args.out, tag + ".pt")
    torch.save({
        "state_dict": policy.state_dict(),
        "rung": args.rung, "level": args.level, "hidden": args.hidden,
        "obs_size": env.obs_size, "n_actions": env.n_actions,
        "n_agents": env.n_agents, "blind_neighbours": blind,
        "n_params": n_params, "args": vars(args), "schema": schema,
    }, ckpt)
    with open(os.path.join(args.out, tag + ".json"), "w") as f:
        json.dump({"tag": tag, "rung": args.rung, "level": args.level,
                   "n_params": n_params, "log": vars(log)}, f, indent=2)
    final = log.ep_ret[-1] if log.ep_ret else float("nan")
    print(f"== done. final rolling ep-return/agent = {final:.3f}. saved {ckpt} ==")


if __name__ == "__main__":
    main()
