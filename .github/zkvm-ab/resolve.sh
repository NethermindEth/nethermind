#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Validates the A/B inputs, resolves both refs to commits on a NethermindEth/nethermind branch,
# fetches the candidate packages and writes the job matrices and cache keys to GITHUB_OUTPUT.
set -euo pipefail

fail() { echo "::error::$*" >&2; exit 1; }

REPO_URL=https://github.com/NethermindEth/nethermind.git
SHA_RE='^[0-9a-f]{40}$'
REF_RE='^[A-Za-z0-9._/-]{1,200}$'
IMAGE_RE='^[a-z0-9][a-z0-9._/-]{0,199}(:[A-Za-z0-9._-]{1,128})?@sha256:[0-9a-f]{64}$'
OPT_RE='^[A-Za-z][A-Za-z0-9_]{0,63}=[A-Za-z0-9*._-]{1,64}$'
ALL_GUESTS=(zisk sp1 openvm airbender)
declare -A projects=(
  [zisk]=Nethermind.Stateless.ZiskGuest
  [sp1]=Nethermind.Stateless.Sp1Guest
  [openvm]=Nethermind.Stateless.OpenVMGuest
  [airbender]=Nethermind.Stateless.AirbenderGuest
)

check_ref() {
  [[ $2 =~ $SHA_RE ]] && return
  [[ $2 =~ $REF_RE && $2 != *..* && $2 != -* && $2 != /* && $2 != */ ]] || fail "$1 must be a branch name or a full commit SHA"
}

BASE_REF=${BASE_REF:-master}
GUESTS=${GUESTS:-zisk,sp1,openvm,airbender}
FORMATS=${FORMATS:-ssz,trusted}

check_ref base_ref "$BASE_REF"
check_ref head_ref "$HEAD_REF"
for name in BFLAT_IMAGE BFLAT_RISCV32_IMAGE; do
  value=${!name}
  [[ -z $value || $value =~ $IMAGE_RE ]] || fail "${name,,} must be an image pinned by digest (name[:tag]@sha256:...)"
done

flags=
count=0
read -ra opts <<< "${CODEGENOPT//,/ }"
for opt in "${opts[@]}"; do
  [[ $opt =~ $OPT_RE ]] || fail "codegenopt entries must be Name=Value"
  flags+="${flags:+ }--codegenopt '$opt'"
  count=$((count + 1))
done
[ "$count" -le 8 ] || fail "codegenopt takes at most 8 entries"

declare -A wanted=()
read -ra items <<< "${GUESTS//,/ }"
for guest in "${items[@]}"; do
  [[ $guest =~ ^(zisk|sp1|openvm|airbender)$ ]] || fail "guests entries must be zisk, sp1, openvm or airbender"
  wanted[$guest]=1
done
selected_guests=()
for guest in "${ALL_GUESTS[@]}"; do
  [[ -z ${wanted[$guest]:-} ]] || selected_guests+=("$guest")
done
[ "${#selected_guests[@]}" -gt 0 ] || fail "guests is empty"

selected_formats=()
read -ra items <<< "${FORMATS//,/ }"
for format in "${items[@]}"; do
  case ${format,,} in
    ssz) [[ " ${selected_formats[*]} " == *" Ssz "* ]] || selected_formats+=(Ssz) ;;
    trusted) [[ " ${selected_formats[*]} " == *" Trusted "* ]] || selected_formats+=(Trusted) ;;
    *) fail "formats entries must be ssz or trusted" ;;
  esac
done
[ "${#selected_formats[@]}" -gt 0 ] || fail "formats is empty"

git init -q "$RUNNER_TEMP/refs"
cd "$RUNNER_TEMP/refs"
git remote add origin "$REPO_URL"
heads=$(git ls-remote --heads origin)
fetched=
resolved=
resolve() {
  local sha
  if [[ $1 =~ $SHA_RE ]]; then
    sha=$1
    if ! awk -v sha="$sha" '$1 == sha { found = 1 } END { exit !found }' <<< "$heads"; then
      if [ -z "$fetched" ]; then
        git fetch -q --filter=tree:0 --no-tags origin '+refs/heads/*:refs/remotes/origin/*'
        fetched=1
      fi
      GIT_NO_LAZY_FETCH=1 git cat-file -e "$sha^{commit}" 2> /dev/null && \
        [ -n "$(git for-each-ref --contains "$sha" --count=1 refs/remotes/origin)" ] || \
        fail "$sha is not on a branch of NethermindEth/nethermind"
    fi
  else
    sha=$(awk -v ref="refs/heads/$1" '$2 == ref { print $1 }' <<< "$heads")
    [ -n "$sha" ] || fail "$1 is not a branch of NethermindEth/nethermind"
  fi
  resolved=$sha
}
resolve "$BASE_REF"
base_sha=$resolved
resolve "$HEAD_REF"
head_sha=$resolved
cd "$GITHUB_WORKSPACE"
echo "base $BASE_REF -> $base_sha"
echo "head $HEAD_REF -> $head_sha"

declare -A extlib_keys=() extlib_paths=()
if [ -n "${EXTLIB//[ ,]/}" ]; then
  fetched_list=$(.github/zkvm-ab/fetch-extlib.sh "$EXTLIB" "$RUNNER_TEMP/candidate")
  while read -r guest key path; do
    extlib_keys[$guest]=$key
    extlib_paths[$guest]=$path
  done <<< "$fetched_list"
fi

format_list=$(IFS=,; echo "${selected_formats[*]}")
keys='{}'
extlib='{}'
build='[]'
measure='[]'
for guest in "${selected_guests[@]}"; do
  image=$BFLAT_IMAGE
  [ "$guest" != airbender ] || image=$BFLAT_RISCV32_IMAGE
  key=$(printf 'base=%s\nimage=%s\nflags=%s\nextlib=%s\nformats=%s\n' \
    "$base_sha" "$image" "$flags" "${extlib_keys[$guest]:-}" "$format_list" | sha256sum | cut -c1-40)
  keys=$(jq -c --arg g "$guest" --arg k "$key" '. + {($g): $k}' <<< "$keys")
  extlib=$(jq -c --arg g "$guest" --arg p "${extlib_paths[$guest]:-}" '. + {($g): $p}' <<< "$extlib")
  for arm in base head; do
    sha=$base_sha
    [ "$arm" = base ] || sha=$head_sha
    build=$(jq -c --arg g "$guest" --arg p "${projects[$guest]}" --arg a "$arm" --arg s "$sha" \
      '. + [{vm: $g, project: $p, arm: $a, sha: $s}]' <<< "$build")
  done
  for format in "${selected_formats[@]}"; do
    measure=$(jq -c --arg g "$guest" --arg p "${projects[$guest]}" --arg f "$format" \
      '. + [{vm: $g, project: $p, format: $f}]' <<< "$measure")
  done
done

{
  echo "base_sha=$base_sha"
  echo "head_sha=$head_sha"
  echo "formats=$format_list"
  echo "guests=$(IFS=,; echo "${selected_guests[*]}")"
  echo "bflat_flags=$flags"
  echo "keys=$keys"
  echo "extlib=$extlib"
  echo "candidate=$([ "${#extlib_keys[@]}" -gt 0 ] && echo true || echo false)"
  echo "openvm=$([ -n "${wanted[openvm]:-}" ] && echo true || echo false)"
  echo "build_matrix=$(jq -c '{include: .}' <<< "$build")"
  echo "measure_matrix=$(jq -c '{include: .}' <<< "$measure")"
} >> "$GITHUB_OUTPUT"
