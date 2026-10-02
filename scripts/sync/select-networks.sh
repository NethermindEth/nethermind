#!/usr/bin/env bash
# Filters a sync matrix (JSON array on stdin) by network name, writing the result to
# stdout. An exact network name selects only that network, so "hoodi" does not also drag
# in "taiko-hoodi"; anything else falls back to substring matching. An empty filter passes
# the matrix through unchanged.
set -euo pipefail

filter="${1-}"

if [ -z "$filter" ]; then
  cat
  exit 0
fi

jq --arg filter "$filter" '
  [.[] | select(.network == $filter)] as $exact
  | if ($exact | length) > 0 then $exact
    else [.[] | select(.network | contains($filter))] end'
