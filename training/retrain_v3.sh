#!/usr/bin/env bash
# Retrain the article's shared policies under schema v3 (obs 233) so they can be
# driven by the current env server for the live-sim recording. Matches the
# saturation settings used for the original v2 runs (demand 1.8, ~600k steps).
set -euo pipefail
cd "$(dirname "$0")/.."
PY=training/.venv/bin/python

echo "== build env server =="
(cd Signal.EnvServer && dotnet build -c Release >/dev/null) && echo "  ok"

echo "== shared corridor (v3) =="
$PY training/train.py --rung shared --level corridor --steps 600000 \
    --demand-lo 1.8 --demand-hi 1.8 --seed 0 --tag shared-corridor-satv3-s0

echo "== shared sc-couplet (v3) =="
$PY training/train.py --rung shared --level sc-couplet --steps 600000 \
    --demand-lo 1.8 --demand-hi 1.8 --seed 0 --tag shared-sc-couplet-satv3-s0

echo "== ALL DONE =="
