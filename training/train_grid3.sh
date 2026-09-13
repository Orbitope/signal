#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
PY=training/.venv/bin/python
(cd Signal.EnvServer && dotnet build -c Release >/dev/null) && echo "built"
$PY training/train.py --rung shared --level grid3 --steps 600000 \
    --demand-lo 0.5 --demand-hi 0.5 --seed 0 --tag shared-grid3-flow-s0
echo "GRID3 DONE"
