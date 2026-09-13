#!/usr/bin/env bash
# Retrain the game's shared policy under the v4 sim rules (permissive lefts
# clear on yellow; major-road lefts yield at two-way stops), so the weights the
# game ships match the sim it runs. Same recipe as train_grid3.sh otherwise.
set -euo pipefail
cd "$(dirname "$0")/.."
PY=training/.venv/bin/python
(cd Signal.EnvServer && dotnet build -c Release >/dev/null) && echo "built"
$PY training/train.py --rung shared --level grid3 --steps 600000 \
    --demand-lo 0.5 --demand-hi 0.5 --seed 0 --tag shared-grid3-flow-v4-s0
echo "GRID3 V4 DONE"
