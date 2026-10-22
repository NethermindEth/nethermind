#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -euo pipefail

dir=$(mktemp -d "$RUNNER_TEMP/geth-genesis.XXXXXX")
for repo in $REPOS; do
  sha=$(gh api "repos/ethpandaops/$repo/commits/HEAD" --jq .sha)
  # Names become file names, so accept only devnet-<n>.
  devnets=$(gh api "repos/ethpandaops/$repo/contents/network-configs?ref=$sha" --jq '.[] | select(.type == "dir") | .name' \
    | { grep -E '^devnet-[0-9]+$' || true; } | sort -V | tail -n "$DEVNETS_PER_REPO")
  if [[ -z "$devnets" ]]; then
    echo "::error::ethpandaops/$repo@$sha has no network-configs/devnet-<n> directories"
    exit 1
  fi
  fetched=0
  while read -r devnet; do
    path="network-configs/$devnet/metadata/genesis.json"
    file="$dir/$repo-$devnet.json"
    status=$(curl --silent --show-error --retry 3 --connect-timeout 30 --max-time 120 \
      -H "Authorization: Bearer $GH_TOKEN" -H "Accept: application/vnd.github.raw" \
      --output "$file" --write-out '%{http_code}' \
      "https://api.github.com/repos/ethpandaops/$repo/contents/$path?ref=$sha")
    case "$status" in
      200)
        echo "Fetched ethpandaops/$repo@$sha $path"
        fetched=$((fetched + 1))
        ;;
      404)
        echo "::warning::ethpandaops/$repo@$sha has no $path"
        rm -f "$file"
        ;;
      *)
        echo "::error::Fetching ethpandaops/$repo@$sha $path returned HTTP $status"
        exit 1
        ;;
    esac
  done <<< "$devnets"
  if [[ "$fetched" -eq 0 ]]; then
    echo "::error::No genesis files fetched from ethpandaops/$repo@$sha"
    exit 1
  fi
done
echo "NETHERMIND_GETH_GENESIS_DIR=$dir" >> "$GITHUB_ENV"
