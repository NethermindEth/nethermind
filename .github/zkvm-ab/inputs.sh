#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Downloads the pinned blocks of <checkout>, checks their sha256 and, for the Trusted format,
# converts them with the converter of the same checkout. <out>/expected.tsv lists, per format,
# the block, the input file, the expected output and its digest.
set -euo pipefail

BLOCKS_URL=https://us-southeast-1.linodeobjects.com/zktests/20261021
checkout=$(realpath "$1")
out=$(realpath -m "$2")
formats=$3
inputs_json="$checkout/src/Nethermind/Nethermind.Stateless.ZiskGuest/inputs.json"

mkdir -p "$out"
: > "$out/expected.tsv"
jq -er '.[] | select((.input | test("^[0-9]+\\.ssz$")) and (.hash | test("^[0-9a-f]{64}$")) and (.output | test("^[0-9a-f]{86}$")) and (.digest | test("^[0-9a-f]{64}$"))) | "\(.input) \(.hash) \(.output) \(.digest)"' "$inputs_json" > "$out/blocks.txt"
[ "$(wc -l < "$out/blocks.txt")" = "$(jq length "$inputs_json")" ] || { echo "::error::invalid pinned block set" >&2; exit 1; }
while read -r input hash output digest; do
  curl --retry 3 -fsSL "$BLOCKS_URL/$input" -o "$out/$input"
  echo "$hash  $out/$input" | sha256sum --status -c - || { echo "::error::checksum of $input failed" >&2; exit 1; }
  printf 'Ssz\t%s\t%s\t%s\t%s\n' "$input" "$input" "$output" "$digest" >> "$out/expected.tsv"
done < "$out/blocks.txt"
rm "$out/blocks.txt"

if [[ ,$formats, == *,Trusted,* ]]; then
  (cd "$checkout" && dotnet run --project tools/StatelessInputGen/StatelessInputGen.csproj -c release -- \
    convert --source "$out" --output "$out") | tee "$RUNNER_TEMP/convert.log"
  grep -E '^[0-9]+\.nti [0-9a-f]+ [0-9a-f]{64}$' "$RUNNER_TEMP/convert.log" | while read -r nti output digest; do
    printf 'Trusted\t%s\t%s\t%s\t%s\n' "${nti%.nti}.ssz" "$nti" "$output" "$digest" >> "$out/expected.tsv"
  done
  [ "$(grep -c '^Trusted' "$out/expected.tsv")" = "$(jq length "$inputs_json")" ] || \
    { echo "::error::the conversion did not produce one trusted input per block" >&2; exit 1; }
fi
sha256sum "$out"/*
cat "$out/expected.tsv"
