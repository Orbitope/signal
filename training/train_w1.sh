#!/usr/bin/env bash
# The game's World 1 light-runner: one shared policy trained on six unconnected
# junctions at once (w1-training in Signal.Core.Levels), demand x0.7-1.3.
set -euo pipefail
cd "$(dirname "$0")/.."
PY=training/.venv/bin/python
(cd Signal.EnvServer && dotnet build -c Release >/dev/null) && echo "built"
$PY training/train.py --rung shared --level w1-training --steps 400000 \
    --demand-lo 0.7 --demand-hi 1.3 --seed 0 --tag shared-w1-v4-s0
echo "W1 DONE"
