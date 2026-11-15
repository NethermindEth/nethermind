#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Runs the guest binary in <guest dir>/bin on every block of <format> and appends one row per
# block to <out>: guest, format, arm, block, metric, cost, output, OK or MISMATCH.
# Usage: measure.sh <guest> <format> <arm> <guest dir> <inputs dir> <out>
# The OpenVM rows come from the zkab-openvm-cost:local image, which reports the trace cost too.
set -euo pipefail

vm=$1
format=$2
arm=$3
guest=$(realpath "$4")
inputs=$(realpath "$5")
out=$6
log="$RUNNER_TEMP/run-$vm-$format-$arm.log"
elf=nethermind
[ "$format" != Trusted ] || elf=nethermind-trusted

chmod +x "$guest/bin/$elf"
rows() { awk -F'\t' -v f="$format" '$1 == f { print $2, $3, $4, $5 }' "$inputs/expected.tsv"; }
emit() { printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$vm" "$format" "$arm" "$@" | tee -a "$out"; }
while read -r _ file _ _; do
  cp "$inputs/$file" "$guest/bin/$file"
done < <(rows)

if [ "$vm" = openvm ]; then
  files=()
  while read -r _ file _ _; do
    make -C "$guest" "$guest/bin/$file.raw" > /dev/null
    files+=("/n/$file.raw")
  done < <(rows)
  rc=0
  docker run --rm --mount type=bind,source="$guest/bin",target=/n zkab-openvm-cost:local "/n/$elf" "${files[@]}" > "$log" 2>&1 || rc=$?
  cat "$log"
  while read -r block file _ digest; do
    line=$(grep "^RESULT /n/$file.raw " "$log" || true)
    instret=$(sed -n 's/.* instret=\([0-9]*\).*/\1/p' <<< "$line")
    cost=$(sed -n 's/.* cost=\([0-9]*\).*/\1/p' <<< "$line")
    got=$(sed -n 's/.* public_values=\([0-9a-f]*\).*/\1/p' <<< "$line")
    ok=$([ "$rc" = 0 ] && [ "$got" = "$digest" ] && echo OK || echo MISMATCH)
    emit "$block" "${instret:--}" "${cost:--}" "${got:--}" "$ok"
  done < <(rows)
  exit 0
fi

while read -r block file output digest; do
  rc=0
  cost=-
  case $vm in
    zisk)
      make -C "$guest" stats INPUT="$file" GUEST_INPUT="$format" > "$log" 2>&1 || rc=$?
      metric=$(sed -n 's/^STEPS[[:space:]]\{1,\}\([0-9][0-9,]*\)[[:space:]]*$/\1/p' "$log" | head -1 | tr -d ,)
      cost=$(sed -n 's/^TOTAL[[:space:]]\{1,\}\([0-9][0-9,]*\)\([[:space:]].*\)\{0,1\}$/\1/p' "$log" | head -1 | tr -d ,)
      got=$(grep -o -m1 "$output" "$log" || true)
      want=$output ;;
    sp1)
      make -C "$guest" run INPUT="$file" GUEST_INPUT="$format" > "$log" 2>&1 || rc=$?
      metric=$(sed -n 's/^cycles \([0-9]*\)$/\1/p' "$log" | head -1)
      got=$(sed -n 's/^output \([0-9a-f]*\)$/\1/p' "$log" | head -1)
      want=$output ;;
    airbender)
      make -C "$guest" verify INPUT="$file" GUEST_INPUT="$format" EXPECTED_DIGEST="$digest" > "$log" 2>&1 || rc=$?
      metric=$(sed -n 's/.*Cycles: \([0-9]*\).*/\1/p' "$log" | head -1)
      got=$([ "$rc" = 0 ] && echo "$digest" || echo FAIL)
      want=$digest ;;
  esac
  ok=$([ "$rc" = 0 ] && [ "$got" = "$want" ] && ! grep -qE 'Exception|Invalid Block' "$log" && echo OK || echo MISMATCH)
  [ "$ok" = OK ] || tail -40 "$log"
  emit "$block" "${metric:--}" "${cost:--}" "${got:--}" "$ok"
done < <(rows)
