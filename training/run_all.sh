#!/usr/bin/env bash
# run_all.sh — reproduce the ladder end-to-end: build the env, train all three
# rungs, evaluate each against same-harness baselines, render the curves.
# Usage:  bash training/run_all.sh
set -euo pipefail
cd "$(dirname "$0")/.."

PY=training/.venv/bin/python
STEPS=${STEPS:-1500000}

echo "== build env server =="
(cd Signal.EnvServer && dotnet build -c Release >/dev/null) && echo "  ok"

echo "== Gate 0: plumbing =="
$PY training/signal_env.py Signal.EnvServer | tail -1

# rung 3 — the shared policy that ships (single intersection + a grid)
$PY training/train.py --rung shared --level fourway-bays --steps "$STEPS" \
    --demand-lo 1.0 --demand-hi 1.0 --tag shared-fourway-bays-fix-s0
$PY training/train.py --rung shared --level grid2 --steps "$STEPS" \
    --demand-lo 1.0 --demand-hi 1.0 --tag shared-grid2-fix-s0

# rung 1 — independent learners on the grid (sharing off, neighbours blinded)
$PY training/train.py --rung independent --level grid2 --steps "$STEPS" \
    --demand-lo 1.0 --demand-hi 1.0 --tag independent-grid2-fix-s0

# rung 2 — CTDE / centralized critic on the grid
$PY training/train.py --rung central --level grid2 --steps "$STEPS" \
    --demand-lo 1.0 --demand-hi 1.0 --tag central-grid2-fix-s0

echo "== evaluations =="
for c in training/runs/*-fix-s0.pt; do $PY training/evaluate.py "$c"; done

echo "== curves =="
$PY training/plot.py training/runs/shared-fourway-bays-fix-s0.json \
    -o training/runs/curve-fourway.svg --title "Signal — shared policy, fourway-bays"
$PY training/plot.py training/runs/*grid2-fix-s0.json \
    -o training/runs/curve-grid2.svg --title "Signal — rungs on grid2 (4 agents)"
echo "== done =="
