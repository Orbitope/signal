#!/usr/bin/env bash
# One brain for the whole game: trained on all-training (World 1 junctions,
# editor pairs/rows/blocks, the couplet, grid3) with demand x0.7-1.3.
set -euo pipefail
cd "$(dirname "$0")/.."
PY=training/.venv/bin/python
(cd Signal.EnvServer && dotnet build -c Release >/dev/null) && echo "built"
$PY training/train.py --rung shared --level all-training --steps 2400000 \
    --demand-lo 0.7 --demand-hi 1.3 --seed 0 --tag shared-all-v4-long-s0
echo "ALL LONG DONE"
