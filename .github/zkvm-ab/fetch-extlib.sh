#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Fetches candidate runtime packages into <dest>/<guest> and prints "<guest> <key> <manifest path>"
# for each, the path relative to <dest>. An entry is <guest>=run:<run id> (an artifact of a
# successful same-repo run of the package repository) or <guest>=<release asset URL> there.
set -euo pipefail

fail() { echo "::error::$*" >&2; exit 1; }

dest=$2
declare -A repos=([zisk]=bflat-libziskos [sp1]=bflat-sp1 [openvm]=bflat-openvm [airbender]=bflat-airbender)
declare -A workflows=(
  [zisk]=.github/workflows/build-release.yml
  [sp1]=.github/workflows/build-release.yml
  [openvm]=.github/workflows/build-release.yml
  [airbender]=.github/workflows/ci.yml
)
declare -A artifacts=([zisk]=nupkg-unsigned [sp1]=nupkg-unsigned [openvm]=nupkg-unsigned [airbender]=libairbender-riscv32)
declare -A seen=()

read -ra entries <<< "${1//,/ }"
[ "${#entries[@]}" -le 4 ] || fail "extlib takes at most one entry per guest"
for entry in "${entries[@]}"; do
  [[ $entry =~ ^(zisk|sp1|openvm|airbender)=(.+)$ ]] || fail "extlib entries must be <guest>=<source>"
  guest=${BASH_REMATCH[1]}
  source=${BASH_REMATCH[2]}
  repo=NethermindEth/${repos[$guest]}
  [[ -z ${seen[$guest]:-} ]] || fail "extlib names $guest twice"
  seen[$guest]=1
  dir="$dest/$guest"
  mkdir -p "$dir/fetched" "$dir/unpacked"

  if [[ $source =~ ^run:([0-9]{1,20})$ ]]; then
    run=${BASH_REMATCH[1]}
    read -r head_repo path conclusion < <(gh api "repos/$repo/actions/runs/$run" \
      --jq '[.head_repository.full_name, .path, .conclusion] | @tsv')
    [ "$head_repo" = "$repo" ] || fail "run $run of $repo was not built from $repo"
    [ "$path" = "${workflows[$guest]}" ] || fail "run $run of $repo is not ${workflows[$guest]}"
    [ "$conclusion" = success ] || fail "run $run of $repo did not succeed"
    gh run download "$run" -R "$repo" -n "${artifacts[$guest]}" -D "$dir/fetched" >&2
  elif [[ $source =~ ^https://github\.com/${repo}/releases/download/[A-Za-z0-9._+-]{1,128}/[A-Za-z0-9._+-]{1,128}\.(nupkg|tar\.gz)$ ]]; then
    curl --retry 3 -fsSL "$source" -o "$dir/fetched/${source##*/}"
  else
    fail "extlib $guest must be run:<id> or a release asset URL of $repo"
  fi

  find "$dir/fetched" -type f -name '*.nupkg' -exec unzip -q -o -d "$dir/unpacked" {} \;
  find "$dir/fetched" -type f -name '*.tar.gz' -exec tar -xzf {} --no-same-owner -C "$dir/unpacked" \;
  find "$dir/fetched" -type f ! -name '*.nupkg' ! -name '*.tar.gz' -exec cp {} "$dir/unpacked/" \;

  if [ "$guest" = airbender ]; then
    mapfile -t manifests < <(find "$dir/unpacked" -type f -name libairbender.bflat.manifest)
  else
    mapfile -t manifests < <(find "$dir/unpacked" -type f -path '*/native/lib*.bflat.manifest')
  fi
  [ "${#manifests[@]}" -eq 1 ] || fail "expected one runtime manifest in the $guest package, found ${#manifests[@]}"
  key=$(cd "$dir/fetched" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -c1-40)
  echo "$guest $key ${manifests[0]#"$dest/"}"
done
