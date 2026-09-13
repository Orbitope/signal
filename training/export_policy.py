"""Export a trained shared policy's actor to the game's flat binary format, plus
a parity fixture the C# side checks itself against.

    python training/export_policy.py training/runs/shared-grid3-flow-s0.pt

Writes godot/policies/<tag>.bin (weights; see Signal.Core.PolicyWeights for the
layout) and godot/policies/<tag>.parity.json (500 synthetic observations with
legal masks, and the greedy action the PyTorch actor picks for each). The C#
LearnedPolicy must reproduce every action; that is the port's gate. Synthetic
observations are shaped like real ones (-1 sentinels where an approach is
missing, [0,1] elsewhere, one-hot phase) so the tanh layers see the same range
they were trained on.
"""
import json
import os
import struct
import sys

import numpy as np
import torch

sys.path.insert(0, os.path.dirname(__file__))
from policies import REGISTRY  # noqa: E402

MAGIC = 0x53474E4C
VERSION = 1


def main():
    ckpt = sys.argv[1]
    out_dir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "..", "godot", "policies")
    os.makedirs(out_dir, exist_ok=True)
    meta = torch.load(ckpt, map_location="cpu", weights_only=False)
    if "schema" not in meta or meta["obs_size"] != 233:
        raise SystemExit(f"{ckpt} is an old observation schema (obs {meta['obs_size']}); the game needs v3 (233)")
    if meta["rung"] != "shared":
        raise SystemExit("only the shared (one-brain) rung exports; the game runs one policy on every light")
    hidden = meta["hidden"]
    policy = REGISTRY["shared"](meta["obs_size"], meta["n_actions"], meta["n_agents"],
                                hidden=(hidden, hidden), **meta.get("schema", {}))
    policy.load_state_dict(meta["state_dict"])
    policy.eval()
    sd = policy.state_dict()
    tag = os.path.splitext(os.path.basename(ckpt))[0]

    def f32(t):
        return np.ascontiguousarray(t.detach().numpy().astype(np.float32)).tobytes()

    w1, b1 = sd["ac.actor.0.weight"], sd["ac.actor.0.bias"]
    w2, b2 = sd["ac.actor.2.weight"], sd["ac.actor.2.bias"]
    w3, b3 = sd["ac.pi.weight"], sd["ac.pi.bias"]
    tag_bytes = tag.encode("utf8")
    blob = struct.pack("<IiI", MAGIC, VERSION, len(tag_bytes)) + tag_bytes
    blob += struct.pack("<iii", meta["obs_size"], hidden, meta["n_actions"])
    for t in (w1, b1, w2, b2, w3, b3):
        blob += f32(t)
    bin_path = os.path.join(out_dir, tag + ".bin")
    with open(bin_path, "wb") as f:
        f.write(blob)

    # Parity fixture.
    rng = np.random.default_rng(12345)
    n = 500
    obs = rng.random((n, meta["obs_size"]), dtype=np.float32)
    # Missing-approach sentinels and zeroed neighbour blocks, like real obs.
    self_floats = meta["schema"]["self_floats"]
    for i in range(n):
        for s in range(8):
            if rng.random() < 0.4:
                obs[i, s * 6:(s + 1) * 6] = [-1, -1, -1, -1, 0, 0]
        ph = np.zeros(8, dtype=np.float32); ph[rng.integers(0, 4)] = 1.0
        obs[i, 48:56] = ph
        for k in range(8):
            if rng.random() < 0.5:
                obs[i, self_floats + k * 22: self_floats + (k + 1) * 22] = 0.0
    mask = np.zeros((n, meta["n_actions"]), dtype=np.float32)
    for i in range(n):
        legal = rng.integers(2, 5)
        mask[i, :legal] = 1.0
        if rng.random() < 0.5:                  # min-green not yet met: only the current phase
            cur = int(obs[i, 48:56].argmax())
            if cur < legal:
                mask[i, :] = 0.0; mask[i, cur] = 1.0
    with torch.no_grad():
        o = torch.as_tensor(obs); m = torch.as_tensor(mask)
        h = policy.ac.actor(o)
        logits = policy.ac.pi(h)
        act = policy.act_greedy(o, m).numpy()
    fixture = {
        "tag": tag, "n": n,
        "obs": obs.round(6).tolist(), "mask": mask.astype(int).tolist(),
        "action": act.tolist(), "logits": logits.numpy().round(5).tolist(),
    }
    fix_path = os.path.join(out_dir, tag + ".parity.json")
    with open(fix_path, "w") as f:
        json.dump(fixture, f)
    n_params = sum(int(t.numel()) for t in (w1, b1, w2, b2, w3, b3))
    print(f"wrote {bin_path} ({len(blob)} bytes, {n_params} actor params) and {fix_path}")


if __name__ == "__main__":
    main()
