#!/usr/bin/env bash
# Builds the client images one scenario ceiling needs, plus the traffic generator.
#
#   images/build.sh 235800                  # Nethermind at that ceiling, and the generator
#   images/build.sh 235800 --only traffic   # just the generator (ceiling-independent)
#   images/build.sh --all                   # every predefined campaign ceiling
#   images/build.sh 235800 --only ethrex    # ethrex at that ceiling (not in the default topology)
#
# Nethermind carries the ceiling as a compile-time constant, so a ceiling change means an image
# change: its runtime --TxPool.FrameTxMaxVerifyGas bounds the declared-gas check only. Images
# are tagged by ceiling and reused across every (role, K_retry) cell of the matrix.
#
# The Nethermind image is the repository's own Dockerfile, built from an export of HEAD with
# patch.sh applied. Uncommitted changes are therefore never in the image, and the recorded
# commit is exactly what was built.
#
# ethrex is opt-in: its main branch still decodes the older frame envelope, which Nethermind
# does not read, so the two cannot share a chain (see UPSTREAM-CANDIDATES.md). The ethrex build
# clones upstream into a scratch directory and drives ethrex's own Dockerfile.
set -euo pipefail

readonly HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly DEVNET_ROOT="$(cd "${HERE}/.." && pwd)"
readonly REPO_ROOT="$(cd "${DEVNET_ROOT}/../.." && pwd)"

readonly CAMPAIGN_CEILINGS=(100000 235800 250000 300000 400000 500000)

ETHREX_REPO="${ETHREX_REPO:-https://github.com/lambdaclass/ethrex}"
ETHREX_REF="${ETHREX_REF:-main}"
WORK_DIR="${FRAME_TX_BUILD_DIR:-${TMPDIR:-/tmp}/frame-tx-devnet-build}"
MANIFEST="${DEVNET_ROOT}/images/build-manifest.json"

usage() {
  sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'
  exit "${1:-0}"
}

only=""
ceilings=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --all) ceilings=("${CAMPAIGN_CEILINGS[@]}"); shift ;;
    --only) only="${2:?--only needs a value: nethermind, ethrex or traffic}"; shift 2 ;;
    -h|--help) usage 0 ;;
    -*) echo "unknown flag $1" >&2; usage 1 ;;
    *) ceilings+=("$1"); shift ;;
  esac
done

if [[ ${#ceilings[@]} -eq 0 && "${only}" != "traffic" ]]; then
  echo "error: give at least one ceiling, or --all, or --only traffic" >&2
  usage 1
fi

wants() {
  if [[ -n "${only}" ]]; then
    [[ "${only}" == "$1" ]]
  else
    [[ "$1" != "ethrex" ]]
  fi
}

record() {
  # Append-only build provenance, so a results directory can be traced back to an image.
  mkdir -p "$(dirname "${MANIFEST}")"
  python3 - "$@" <<'PY'
import json, os, sys, datetime
manifest, image, *pairs = sys.argv[1:]
entry = {"image": image, "built_at": datetime.datetime.now(datetime.timezone.utc).isoformat()}
entry.update(dict(p.split("=", 1) for p in pairs))
data = []
if os.path.exists(manifest):
    with open(manifest) as handle:
        try:
            data = json.load(handle)
        except ValueError:
            data = []
data = [e for e in data if e.get("image") != image] + [entry]
with open(manifest, "w") as handle:
    json.dump(data, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY
}

build_traffic() {
  local tag="frame-tx-devnet/traffic:local"
  echo "==> building ${tag}"
  docker build \
    -f "${DEVNET_ROOT}/images/traffic/Dockerfile" \
    -t "${tag}" \
    "${DEVNET_ROOT}"
  record "${MANIFEST}" "${tag}" "kind=traffic"
}

build_nethermind() {
  local ceiling="$1"
  local tag="frame-tx-devnet/nethermind:vg${ceiling}"
  local commit
  commit=$(git -C "${REPO_ROOT}" rev-parse HEAD)
  if [[ -n "$(git -C "${REPO_ROOT}" status --porcelain -- src/Nethermind)" ]]; then
    echo "note: src/Nethermind has uncommitted changes; the image is built from ${commit} without them"
  fi

  local context="${WORK_DIR}/nethermind-${ceiling}"
  rm -rf "${context}"
  mkdir -p "${context}"
  git -C "${REPO_ROOT}" archive "${commit}" Dockerfile global.json nuget.config Directory.Build.props \
    Directory.Build.targets Directory.Packages.props src/Nethermind scripts/entrypoint.sh | tar -x -C "${context}"
  bash "${DEVNET_ROOT}/images/nethermind/patch.sh" "${context}/src/Nethermind" "${ceiling}"

  echo "==> building ${tag} from ${REPO_ROOT} @ ${commit}"
  docker build \
    -f "${context}/Dockerfile" \
    --build-arg "COMMIT_HASH=${commit}" \
    --label "org.nethermind.frame_tx.max_verify_gas=${ceiling}" \
    -t "${tag}" \
    "${context}"
  rm -rf "${context}"
  record "${MANIFEST}" "${tag}" "kind=nethermind" "max_verify_gas=${ceiling}" "source_commit=${commit}"
}

build_ethrex() {
  local ceiling="$1"
  local tag="frame-tx-devnet/ethrex:vg${ceiling}"
  local checkout="${WORK_DIR}/ethrex"

  mkdir -p "${WORK_DIR}"
  if [[ -d "${checkout}/.git" ]]; then
    git -C "${checkout}" fetch --depth 1 origin "${ETHREX_REF}"
  else
    rm -rf "${checkout}"
    git clone --depth 1 --branch "${ETHREX_REF}" "${ETHREX_REPO}" "${checkout}"
    git -C "${checkout}" fetch --depth 1 origin "${ETHREX_REF}"
  fi
  # Hard reset rather than pull: the previous ceiling's patch is still in the tree.
  git -C "${checkout}" reset --hard FETCH_HEAD
  git -C "${checkout}" clean -fd

  local sha
  sha=$(git -C "${checkout}" rev-parse HEAD)
  echo "==> building ${tag} from ${ETHREX_REPO}@${ETHREX_REF} (${sha})"

  bash "${DEVNET_ROOT}/images/ethrex/patch.sh" "${checkout}" "${ceiling}"

  docker build \
    -f "${checkout}/Dockerfile" \
    --build-arg "GIT_SHA=${sha}" \
    --build-arg "GIT_BRANCH=${ETHREX_REF}" \
    -t "${tag}" \
    "${checkout}"
  record "${MANIFEST}" "${tag}" "kind=ethrex" "max_verify_gas=${ceiling}" \
    "source_repo=${ETHREX_REPO}" "source_ref=${ETHREX_REF}" "source_commit=${sha}"
}

if wants traffic; then
  build_traffic
fi

for ceiling in "${ceilings[@]}"; do
  if ! [[ "${ceiling}" =~ ^[1-9][0-9]*$ ]]; then
    echo "error: '${ceiling}' is not a positive integer ceiling" >&2
    exit 1
  fi
  wants nethermind && build_nethermind "${ceiling}"
  wants ethrex && build_ethrex "${ceiling}"
done

echo
echo "build manifest: ${MANIFEST}"
