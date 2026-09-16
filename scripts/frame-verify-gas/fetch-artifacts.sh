#!/usr/bin/env bash
# Usage: REQUESTED_VERSION=vX.Y.Z RUNNER_TEMP=... GITHUB_ENV=... [GH_TOKEN=...] fetch-artifacts.sh
# GH_TOKEN is optional: the release and its assets are public. Set it only to raise the
# api.github.com call below off the unauthenticated per-IP rate limit on a busy shared runner.
set -uo pipefail

readonly REPO=NethermindEth/frame-verify-gas
readonly LABELS=(236k 300k 500k soispoke)
readonly MAX_EXPANDED_BYTES=$((16 * 1024 * 1024))

version="${REQUESTED_VERSION:-}"
version="${version//[[:space:]]/}"
if [[ -z "${version}" ]]; then
  echo "::error::groth16_artifacts_version is empty. Supply a ${REPO} release tag, e.g. v1.0.0."
  exit 1
fi
if [[ ! "${version}" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "::error::groth16_artifacts_version '${version}' is not a vMAJOR.MINOR.PATCH release tag."
  exit 1
fi
for tool in curl python3 sha256sum tar; do
  if ! command -v "${tool}" >/dev/null; then
    echo "::error::${tool} is not installed on this runner."
    exit 1
  fi
done
for var in RUNNER_TEMP GITHUB_ENV; do
  if [[ -z "${!var:-}" ]]; then
    echo "::error::${var} is not set."
    exit 1
  fi
done

artifacts="${RUNNER_TEMP}/frame-verify-gas-groth16"
work=$(mktemp -d "${RUNNER_TEMP}/frame-verify-gas-download.XXXXXX") || exit 1
trap 'rm -rf "${work}"' EXIT
assets="${work}/assets"
mkdir "${assets}" || exit 1

# No token required: ${REPO} and its releases are public. GH_TOKEN, when present, is sent anyway
# to move this call off the 60/req-per-hour-per-IP anonymous bucket onto the authenticated one
# shared runners can otherwise exhaust (see run-nethtest.yml's "Resolve fixture releases" step).
auth_header=()
if [[ -n "${GH_TOKEN:-}" ]]; then
  auth_header=(-H "Authorization: Bearer ${GH_TOKEN}")
fi
if ! release_json=$(curl -fsS "${auth_header[@]}" "https://api.github.com/repos/${REPO}/releases/tags/${version}" 2>"${work}/curl.err"); then
  echo "::error::Release ${version} could not be read from ${REPO}: $(paste -sd ' ' "${work}/curl.err")"
  exit 1
fi
if ! state=$(python3 -c '
import json, sys
data = json.load(sys.stdin)
print("true" if data["draft"] else "false", "true" if data["prerelease"] else "false")
' <<< "${release_json}" 2>"${work}/curl.err"); then
  echo "::error::Release ${version} on ${REPO} returned unparsable JSON: $(paste -sd ' ' "${work}/curl.err")"
  exit 1
fi
case "${state}" in
  'false false') ;;
  'true '*) echo "::error::Release ${version} on ${REPO} is a draft. Supply a published release."; exit 1 ;;
  *' true') echo "::error::Release ${version} on ${REPO} is a prerelease. Supply a full release."; exit 1 ;;
  *) echo "::error::Release ${version} on ${REPO} has unexpected draft/prerelease state '${state}'."; exit 1 ;;
esac

tarballs=("${LABELS[@]/#/sweep-}")
tarballs=("${tarballs[@]/%/.tar.gz}")
# Asset download URLs come from the same JSON already fetched above, so listing assets never
# costs a second api.github.com call.
asset_urls=$(python3 -c '
import json, sys
data = json.load(sys.stdin)
for a in data.get("assets", []):
    print(a["name"] + "\t" + a["browser_download_url"])
' <<< "${release_json}")
for asset in SHA256SUMS "${tarballs[@]}"; do
  url=$(awk -F'\t' -v n="${asset}" '$1 == n { print $2; exit }' <<< "${asset_urls}")
  if [[ -z "${url}" ]]; then
    echo "::error::Release ${version} on ${REPO} has no ${asset} asset."
    exit 1
  fi
  if ! curl -fsSL -o "${assets}/${asset}" "${url}" 2>"${work}/curl.err"; then
    echo "::error::Downloading ${asset} from release ${version} of ${REPO} failed: $(paste -sd ' ' "${work}/curl.err")"
    exit 1
  fi
done

expected=$(printf '%s\n' "${tarballs[@]}" | sort)
listed=$(sed -E 's/^[0-9a-f]{64} [ *]//' "${assets}/SHA256SUMS" | sort)
if grep -qvE '^[0-9a-f]{64} [ *]sweep-[a-z0-9]+\.tar\.gz$' "${assets}/SHA256SUMS" || [[ "${listed}" != "${expected}" ]]; then
  echo "::error::SHA256SUMS of release ${version} must list exactly: $(paste -sd ' ' <<< "${expected}")."
  exit 1
fi
if ! (cd "${assets}" && sha256sum --strict --quiet -c SHA256SUMS); then
  echo "::error::The downloaded tarballs do not match SHA256SUMS of release ${version}; the download is corrupt, partial or was altered. Re-dispatch."
  exit 1
fi

if ! { rm -rf "${artifacts}" && mkdir -p "${artifacts}"; }; then
  echo "::error::Could not create ${artifacts}."
  exit 1
fi
for label in "${LABELS[@]}"; do
  sweep="sweep-${label}"
  tarball="${assets}/${sweep}.tar.gz"
  if ! names=$(tar -tzf "${tarball}") || ! listing=$(tar -tvzf "${tarball}") || [[ -z "${names}" ]]; then
    echo "::error::${sweep}.tar.gz is not a readable, non-empty gzip tarball; the download is corrupt."
    exit 1
  fi
  # tar escapes control characters in listings, so each line is exactly one member.
  bad=$(grep -vE "^(\./|(\./)?${sweep}(/[A-Za-z0-9._-]+)*/?)\$" <<< "${names}"; grep -E '(^|/)\.\.(/|$)' <<< "${names}")
  if [[ -n "${bad}" ]]; then
    echo "::error::${sweep}.tar.gz has a member with an unexpected path or prefix ('${bad%%$'\n'*}'); only ${sweep}/ or ./${sweep}/ without '..' is allowed."
    exit 1
  fi
  if [[ "$(wc -l <<< "${names}")" != "$(wc -l <<< "${listing}")" ]]; then
    echo "::error::${sweep}.tar.gz has a member name that tar lists differently with -t and -tv, so it cannot be checked; refusing to extract it."
    exit 1
  fi
  if grep -qv '^[-d]' <<< "${listing}"; then
    echo "::error::${sweep}.tar.gz has a link or special-file member; only regular files and directories are allowed."
    exit 1
  fi
  expanded=$(awk '{ total += $3 } END { print total + 0 }' <<< "${listing}")
  if (( expanded > MAX_EXPANDED_BYTES )); then
    echo "::error::${sweep}.tar.gz expands to ${expanded} bytes, over the ${MAX_EXPANDED_BYTES}-byte limit."
    exit 1
  fi
  mkdir "${work}/${sweep}" || exit 1
  if ! tar -xzf "${tarball}" -C "${work}/${sweep}" --no-same-owner --no-same-permissions \
    || [[ "$(ls -A "${work}/${sweep}")" != "${sweep}" ]] \
    || [[ -n "$(find "${work}/${sweep}" -mindepth 1 ! -type f ! -type d -print -quit)" ]]; then
    echo "::error::${sweep}.tar.gz did not extract to plain files under ${sweep}/ only."
    exit 1
  fi
  mv "${work}/${sweep}/${sweep}" "${artifacts}/${sweep}" || exit 1
  for file in verifier.hex calldata-invalid.hex gas.txt; do
    if [[ ! -f "${artifacts}/${sweep}/${file}" ]]; then
      echo "::error::${sweep}.tar.gz has no ${sweep}/${file}."
      exit 1
    fi
  done
done

if ! python3 "$(dirname "${BASH_SOURCE[0]}")/check-verifiers.py" "${artifacts}" "${LABELS[@]}"; then
  echo "::error::The plausibility heuristic in scripts/frame-verify-gas/check-verifiers.py rejected release ${version}; see the errors above."
  exit 1
fi

sums="${RUNNER_TEMP}/frame-verify-gas-groth16.SHA256SUMS"
cp "${assets}/SHA256SUMS" "${sums}" || exit 1
digest=$(sha256sum "${assets}/SHA256SUMS" | cut -d ' ' -f 1) || exit 1
if ! {
  echo "FRAME_GROTH16_ARTIFACTS=${artifacts}"
  echo "GROTH16_ARTIFACTS_SHA256SUMS=${sums}"
  echo "GROTH16_ARTIFACTS_VERSION=${version}"
  echo "GROTH16_ARTIFACTS_SHA256SUMS_DIGEST=${digest}"
} >> "${GITHUB_ENV}"; then
  echo "::error::Failed to write artifact paths to GITHUB_ENV; the measurement step would otherwise run without FRAME_GROTH16_ARTIFACTS set and self-ignore the privacy cases silently."
  exit 1
fi
echo "Groth16 artifacts of ${REPO}@${version} extracted to ${artifacts}."
