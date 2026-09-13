"""
policies.py — actor-critic networks and the three rung "policies" for Signal.

The env server hands the trainer a batch of (n_envs, n_agents) decision points,
each with a 105-float local observation, an 8-wide legality mask, and a scalar
pressure reward. Every rung in signal-training-plan.md is a different answer to
one question: *which parameters are shared, and what does the critic see?*

  rung 3  SharedPolicy       one actor-critic, every agent slot shares it.
  rung 1  IndependentPolicy  one actor-critic PER agent slot (neighbours masked
                             out of the obs, so each only sees itself).
  rung 2  CentralCriticPolicy (CTDE / MAPPO) shared local actor + a centralized
                             critic that sees the whole network during training.

All three expose the same tiny interface the PPO core drives:

    act(obs, mask)                 -> action, logp, value        (rollout time)
    evaluate(obs, gobs, act, mask) -> logp, entropy, value       (update  time)
    parameters()                                                 (for the optimizer)

Shapes: obs (E, A, O), mask (E, A, P), gobs (E, A*O). Everything is torch.
"""
from __future__ import annotations

import torch
import torch.nn as nn
from torch.distributions import Categorical

NEG = -1e9  # additive logit for an illegal action; softmax drives it to ~0


def _mlp(sizes, act=nn.Tanh):
    layers = []
    for i in range(len(sizes) - 1):
        layers.append(nn.Linear(sizes[i], sizes[i + 1]))
        if i < len(sizes) - 2:
            layers.append(act())
    return nn.Sequential(*layers)


def _ortho(module, gain):
    for m in module.modules():
        if isinstance(m, nn.Linear):
            nn.init.orthogonal_(m.weight, gain)
            nn.init.zeros_(m.bias)
    return module


def masked_dist(logits, mask):
    """Categorical over legal actions only. `mask` is 1 where legal."""
    return Categorical(logits=logits + (1.0 - mask) * NEG)


class ActorCritic(nn.Module):
    """Separate 105 -> h -> h actor and critic trunks, an 8-way policy head and
    a scalar value head.

    Separate (not shared) trunks on purpose: the pressure return is large in
    magnitude (~-200), so a shared trunk lets the value-fitting gradient swamp
    the small normalized policy gradient and the policy collapses to a constant
    argmax. Decoupling costs ~2x params (still ~62k at h=128 — a quarter-MB, far
    under the plan's budget) and is the standard, stable PPO default."""

    def __init__(self, obs_dim, n_actions, hidden=(128, 128)):
        super().__init__()
        self.actor = _ortho(_mlp((obs_dim, *hidden)), gain=2.0 ** 0.5)
        self.actor.append(nn.Tanh())
        self.critic = _ortho(_mlp((obs_dim, *hidden)), gain=2.0 ** 0.5)
        self.critic.append(nn.Tanh())
        self.pi = _ortho(nn.Linear(hidden[-1], n_actions), gain=0.01)
        self.vf = _ortho(nn.Linear(hidden[-1], 1), gain=1.0)

    def forward(self, obs):
        return self.pi(self.actor(obs)), self.vf(self.critic(obs)).squeeze(-1)

    def actor_logits(self, obs):
        return self.pi(self.actor(obs))


class SharedPolicy(nn.Module):
    """Rung 3. One network; agents are just extra rows in the batch."""

    kind = "shared"

    def __init__(self, obs_dim, n_actions, n_agents, hidden=(128, 128), **_):
        super().__init__()
        self.ac = ActorCritic(obs_dim, n_actions, hidden)
        self.n_agents = n_agents

    @torch.no_grad()
    def act(self, obs, mask):
        logits, value = self.ac(obs)
        dist = masked_dist(logits, mask)
        action = dist.sample()
        return action, dist.log_prob(action), value

    def evaluate(self, obs, gobs, action, mask):
        logits, value = self.ac(obs)
        dist = masked_dist(logits, mask)
        return dist.log_prob(action), dist.entropy(), value

    @torch.no_grad()
    def act_greedy(self, obs, mask):
        logits, _ = self.ac(obs)
        return (logits + (1.0 - mask) * NEG).argmax(-1)


class IndependentPolicy(nn.Module):
    """Rung 1. A separate actor-critic per agent slot. Envs of the same slot
    still share (they are the *same* intersection under different traffic), but
    slots do not. Neighbour blocks are zeroed by the trainer so each brain sees
    only itself — this is rung 3 with sharing off and coordination blinded."""

    kind = "independent"

    def __init__(self, obs_dim, n_actions, n_agents, hidden=(128, 128), **_):
        super().__init__()
        self.nets = nn.ModuleList(
            [ActorCritic(obs_dim, n_actions, hidden) for _ in range(n_agents)])
        self.n_agents = n_agents

    def _per_slot(self, obs, mask, fn):
        # obs (E, A, O): run slot a through net a, restack on the A axis.
        outs = [fn(self.nets[a], obs[:, a], mask[:, a]) for a in range(self.n_agents)]
        return [torch.stack(t, dim=1) for t in zip(*outs)]

    @torch.no_grad()
    def act(self, obs, mask):
        def fn(net, o, m):
            logits, value = net(o)
            dist = masked_dist(logits, m)
            a = dist.sample()
            return a, dist.log_prob(a), value
        return self._per_slot(obs, mask, fn)

    def evaluate(self, obs, gobs, action, mask):
        def fn_eval(a):
            def fn(net, o, m):
                logits, value = net(o)
                dist = masked_dist(logits, m)
                return dist.log_prob(action[:, a]), dist.entropy(), value
            return fn
        outs = [fn_eval(a)(self.nets[a], obs[:, a], mask[:, a])
                for a in range(self.n_agents)]
        return [torch.stack(t, dim=1) for t in zip(*outs)]

    @torch.no_grad()
    def act_greedy(self, obs, mask):
        cols = []
        for a in range(self.n_agents):
            logits, _ = self.nets[a](obs[:, a])
            cols.append((logits + (1.0 - mask[:, a]) * NEG).argmax(-1))
        return torch.stack(cols, dim=1)


class CentralCriticPolicy(nn.Module):
    """Rung 2 (CTDE / MAPPO). The actor is exactly rung 3's shared local actor,
    so the trained actor drops straight onto the shared-policy deployment path.
    A separate centralized critic sees every agent's obs (the global state) and
    scores each agent — it exists only during training and resolves the credit
    assignment that one scalar reward across many heads cannot."""

    kind = "central"

    def __init__(self, obs_dim, n_actions, n_agents, hidden=(128, 128),
                 critic_hidden=(256, 256), **_):
        super().__init__()
        self.actor = ActorCritic(obs_dim, n_actions, hidden)  # value head unused
        self.n_agents = n_agents
        self.critic = _ortho(_mlp((obs_dim * n_agents, *critic_hidden, n_agents)),
                             gain=2.0 ** 0.5)

    @torch.no_grad()
    def act(self, obs, mask):
        logits, _ = self.actor(obs)
        dist = masked_dist(logits, mask)
        action = dist.sample()
        gobs = obs.reshape(obs.shape[0], -1)
        value = self.critic(gobs)  # (E, A)
        return action, dist.log_prob(action), value

    def evaluate(self, obs, gobs, action, mask):
        logits, _ = self.actor(obs)
        dist = masked_dist(logits, mask)
        value = self.critic(gobs)
        return dist.log_prob(action), dist.entropy(), value

    @torch.no_grad()
    def act_greedy(self, obs, mask):
        logits, _ = self.actor(obs)
        return (logits + (1.0 - mask) * NEG).argmax(-1)


class AttnActorCritic(nn.Module):
    """Coupling-aware actor: instead of flattening the 8 neighbour slots into the
    MLP, it attends over them. Self features form the query; each neighbour block
    is a key/value; softmax attention (masked to *valid* neighbours) aggregates a
    context vector that is concatenated with the self embedding before the policy
    head. The critic stays a plain MLP over the full obs (separate path, so the
    large value gradient never swamps the actor). This is a 1-hop graph-attention
    layer — the natural step past a fixed-slot MLP for tightly-coupled junctions."""

    def __init__(self, self_floats, nb_floats, max_nb, n_actions, d=64, hidden=(128, 128)):
        super().__init__()
        self.sf, self.nf, self.mnb, self.d = self_floats, nb_floats, max_nb, d
        self.self_enc = _ortho(_mlp((self_floats, d)), 2 ** 0.5); self.self_enc.append(nn.Tanh())
        self.nb_enc = _ortho(_mlp((nb_floats, d)), 2 ** 0.5); self.nb_enc.append(nn.Tanh())
        self.q = _ortho(nn.Linear(d, d), 1.0)
        self.k = _ortho(nn.Linear(d, d), 1.0)
        self.v = _ortho(nn.Linear(d, d), 1.0)
        self.actor = _ortho(_mlp((2 * d, *hidden)), 2 ** 0.5); self.actor.append(nn.Tanh())
        self.pi = _ortho(nn.Linear(hidden[-1], n_actions), 0.01)
        self.critic = _ortho(_mlp((self_floats + max_nb * nb_floats, *hidden)), 2 ** 0.5)
        self.critic.append(nn.Tanh())
        self.vf = _ortho(nn.Linear(hidden[-1], 1), 1.0)

    def forward(self, obs):
        s = obs[..., :self.sf]
        nb = obs[..., self.sf:].reshape(*obs.shape[:-1], self.mnb, self.nf)
        valid = nb[..., -1]                              # (..., mnb) validity flag
        se = self.self_enc(s)                            # (..., d)
        ne = self.nb_enc(nb)                             # (..., mnb, d)
        att = (self.q(se).unsqueeze(-2) * self.k(ne)).sum(-1) / (self.d ** 0.5)
        att = att.masked_fill(valid < 0.5, -1e9)
        w = torch.softmax(att, -1).unsqueeze(-1)         # (..., mnb, 1)
        has_nb = (valid.sum(-1, keepdim=True) > 0).float()
        ctx = (w * self.v(ne)).sum(-2) * has_nb          # (..., d), 0 if no neighbours
        logits = self.pi(self.actor(torch.cat([se, ctx], -1)))
        value = self.vf(self.critic(obs)).squeeze(-1)
        return logits, value


class SharedAttnPolicy(nn.Module):
    """Rung 'attn'. One shared neighbour-attention actor-critic across all agents."""

    kind = "attn"

    def __init__(self, obs_dim, n_actions, n_agents, hidden=(128, 128),
                 self_floats=57, neighbor_floats=22, max_neighbors=8, **_):
        super().__init__()
        self.ac = AttnActorCritic(self_floats, neighbor_floats, max_neighbors,
                                  n_actions, hidden=hidden)
        self.n_agents = n_agents

    @torch.no_grad()
    def act(self, obs, mask):
        logits, value = self.ac(obs)
        dist = masked_dist(logits, mask)
        action = dist.sample()
        return action, dist.log_prob(action), value

    def evaluate(self, obs, gobs, action, mask):
        logits, value = self.ac(obs)
        dist = masked_dist(logits, mask)
        return dist.log_prob(action), dist.entropy(), value

    @torch.no_grad()
    def act_greedy(self, obs, mask):
        logits, _ = self.ac(obs)
        return (logits + (1.0 - mask) * NEG).argmax(-1)


REGISTRY = {
    "shared": SharedPolicy,
    "independent": IndependentPolicy,
    "central": CentralCriticPolicy,
    "attn": SharedAttnPolicy,
}
