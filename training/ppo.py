"""
ppo.py — a compact multi-agent PPO over the Signal env server.

One loop trains all three rungs; the only thing that changes is the `policy`
object (see policies.py). Every (env, agent) pair is an independent actor along
time — parameter sharing happens inside the policy, not here. That is the whole
reason the shared policy gets N-times the experience per gradient step.

Not SB3: masked multi-agent action spaces with shared parameters and a
centralized critic are cleaner hand-rolled than bent around a single-agent API,
and the plan wants every number here measured and legible.
"""
from __future__ import annotations

import time
from dataclasses import dataclass, field

import numpy as np
import torch


@dataclass
class PPOConfig:
    total_steps: int = 400_000      # env decision-tuples (per agent) to collect
    rollout: int = 128              # decision-steps per policy update
    epochs: int = 4
    minibatches: int = 4
    gamma: float = 0.99
    gae_lambda: float = 0.95
    clip: float = 0.2
    vf_coef: float = 0.5
    ent_coef: float = 0.01
    lr: float = 3e-4
    max_grad_norm: float = 0.5
    device: str = "cpu"
    log_every: int = 1
    seed: int = 0


# obs layout (see Observations.cs): [0:33] self, [33:105] the four neighbour
# blocks. Rung 1 blinds an agent to its neighbours by zeroing that tail.
SELF_OBS = 33


@dataclass
class TrainLog:
    updates: list = field(default_factory=list)
    steps: list = field(default_factory=list)
    ret: list = field(default_factory=list)          # mean episodic return/agent
    ep_ret: list = field(default_factory=list)        # rolling mean episode return
    entropy: list = field(default_factory=list)
    value_loss: list = field(default_factory=list)
    sps: list = field(default_factory=list)


def train(env, policy, cfg: PPOConfig, blind_neighbours=False, log=None,
          on_update=None):
    dev = torch.device(cfg.device)
    policy.to(dev)
    torch.manual_seed(cfg.seed)
    opt = torch.optim.Adam(policy.parameters(), lr=cfg.lr, eps=1e-5)
    log = log or TrainLog()

    E, A, O = env.n_envs, env.n_agents, env.obs_size
    P = env.n_actions
    B = E * A
    T = cfg.rollout

    def to_t(x, dtype=torch.float32):
        return torch.as_tensor(np.ascontiguousarray(x), dtype=dtype, device=dev)

    obs_np, mask_np = env.reset()
    self_obs = getattr(env, "self_floats", SELF_OBS)   # schema v3 boundary; else v2 default

    def prep(o):
        o = to_t(o)
        if blind_neighbours:
            o = o.clone()
            o[..., self_obs:] = 0.0
        return o

    obs = prep(obs_np)
    mask = to_t(mask_np)

    # rollout buffers, time-major
    b_obs = torch.zeros((T, E, A, O), device=dev)
    b_gobs = torch.zeros((T, E, A * O), device=dev)
    b_mask = torch.zeros((T, E, A, P), device=dev)
    b_act = torch.zeros((T, E, A), dtype=torch.long, device=dev)
    b_logp = torch.zeros((T, E, A), device=dev)
    b_val = torch.zeros((T, E, A), device=dev)
    b_rew = torch.zeros((T, E, A), device=dev)
    b_done = torch.zeros((T, E), device=dev)

    # per-episode return accounting (agent-summed reward over an episode)
    running_ret = np.zeros((E, A), dtype=np.float64)
    ep_returns: list[float] = []

    n_updates = cfg.total_steps // (T * E)
    global_step = 0
    t_start = time.time()

    for update in range(1, n_updates + 1):
        for t in range(T):
            action, logp, value = policy.act(obs, mask)
            b_obs[t] = obs
            b_gobs[t] = obs.reshape(E, -1)
            b_mask[t] = mask
            b_act[t] = action
            b_logp[t] = logp
            b_val[t] = value

            obs_np, rew_np, mask_np, done_np = env.step(action.cpu().numpy())
            b_rew[t] = to_t(rew_np)
            b_done[t] = to_t(done_np.astype(np.float32))

            running_ret += rew_np
            for e in range(E):
                if done_np[e]:
                    ep_returns.extend(running_ret[e].tolist())
                    running_ret[e] = 0.0

            obs = prep(obs_np)
            mask = to_t(mask_np)
            global_step += E

        # bootstrap value for the final obs
        with torch.no_grad():
            _, _, last_val = policy.act(obs, mask)

        # GAE per (env, agent), truncating at episode boundaries
        adv = torch.zeros((T, E, A), device=dev)
        last_gae = torch.zeros((E, A), device=dev)
        for t in reversed(range(T)):
            nonterminal = (1.0 - b_done[t]).unsqueeze(-1)  # (E,1) -> broadcast to A
            next_val = last_val if t == T - 1 else b_val[t + 1]
            delta = b_rew[t] + cfg.gamma * next_val * nonterminal - b_val[t]
            last_gae = delta + cfg.gamma * cfg.gae_lambda * nonterminal * last_gae
            adv[t] = last_gae
        ret = adv + b_val

        # flatten (T,E,A) actors into one batch
        f_obs = b_obs.reshape(T * E, A, O)
        f_gobs = b_gobs.reshape(T * E, A * O)
        f_mask = b_mask.reshape(T * E, A, P)
        f_act = b_act.reshape(T * E, A)
        f_logp = b_logp.reshape(T * E, A)
        f_adv = adv.reshape(T * E, A)
        f_ret = ret.reshape(T * E, A)
        f_val = b_val.reshape(T * E, A)

        # normalize advantages over all live actors
        a_mean, a_std = f_adv.mean(), f_adv.std().clamp_min(1e-8)
        f_adv = (f_adv - a_mean) / a_std

        N = T * E
        idx = np.arange(N)
        mb = N // cfg.minibatches
        ent_acc = vloss_acc = 0.0
        n_steps_upd = 0
        for _ in range(cfg.epochs):
            np.random.shuffle(idx)
            for start in range(0, N, mb):
                j = idx[start:start + mb]
                new_logp, entropy, new_val = policy.evaluate(
                    f_obs[j], f_gobs[j], f_act[j], f_mask[j])

                ratio = (new_logp - f_logp[j]).exp()
                a = f_adv[j]
                l1 = ratio * a
                l2 = torch.clamp(ratio, 1 - cfg.clip, 1 + cfg.clip) * a
                pg_loss = -torch.min(l1, l2).mean()

                # clipped value loss
                v = new_val
                v_clip = f_val[j] + (v - f_val[j]).clamp(-cfg.clip, cfg.clip)
                vl = torch.max((v - f_ret[j]) ** 2, (v_clip - f_ret[j]) ** 2)
                v_loss = 0.5 * vl.mean()

                ent = entropy.mean()
                loss = pg_loss + cfg.vf_coef * v_loss - cfg.ent_coef * ent

                opt.zero_grad()
                loss.backward()
                torch.nn.utils.clip_grad_norm_(policy.parameters(), cfg.max_grad_norm)
                opt.step()

                ent_acc += ent.item(); vloss_acc += v_loss.item(); n_steps_upd += 1

        sps = global_step / (time.time() - t_start)
        rolling = float(np.mean(ep_returns[-200:])) if ep_returns else float("nan")
        log.updates.append(update)
        log.steps.append(global_step)
        log.ep_ret.append(rolling)
        log.entropy.append(ent_acc / n_steps_upd)
        log.value_loss.append(vloss_acc / n_steps_upd)
        log.sps.append(sps)
        if update % cfg.log_every == 0:
            print(f"  upd {update:3d}/{n_updates}  step {global_step:>8,}  "
                  f"ep_ret {rolling:8.3f}  ent {log.entropy[-1]:.3f}  "
                  f"vloss {log.value_loss[-1]:8.2f}  {sps:,.0f} step/s")
        if on_update:
            on_update(update, log)

    return log
