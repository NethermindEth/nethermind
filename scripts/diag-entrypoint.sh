#!/usr/bin/env bash
set -euo pipefail

mkdir -p /nethermind/diag/dottrace
exec dottrace start \
  --framework=NetCore \
  --profiling-type=Timeline \
  --save-to=/nethermind/diag/dottrace/nethermind_$(date +%F_%H-%M-%S).dtt \
  --service-output=on \
  --propagate-exit-code \
  -- ./nethermind "$@"
