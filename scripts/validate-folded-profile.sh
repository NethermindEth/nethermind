#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Require at least one stackcollapse-style row with a positive sample count.
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "usage: $(basename "$0") <perf.folded>" >&2
  exit 2
fi

profile="$1"
if [[ ! -f "$profile" ]]; then
  echo "error: folded profile '$profile' does not exist" >&2
  exit 1
fi

if ! awk '
  {
    line = $0
    sub(/\r$/, "", line)
    if (line ~ /^[^[:space:];][^;]*;[^[:space:]].*[[:space:]][1-9][0-9]*[[:space:]]*$/) {
      found = 1
      exit
    }
  }
  END { exit !found }
' "$profile"; then
  echo "error: folded profile '$profile' has no positive-sample stack" >&2
  exit 1
fi
