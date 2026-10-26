#!/usr/bin/env bash
# Builds the client images one scenario ceiling needs, plus the traffic generator.
#
#   images/build.sh 322800                  # both clients at that ceiling, and the generator
#   images/build.sh 322800 --only traffic   # just the generator (ceiling-independent)
#   images/build.sh --all                   # every predefined campaign ceiling
#
# Both clients carry the ceiling as a compile-time constant, so a ceiling change means an image
# change: Nethermind's runtime --TxPool.FrameTxMaxVerifyGas bounds the declared-gas check only,
# and ethrex has no runtime knob at all. Images are tagged by ceiling and reused across every
# (role, K_retry) cell of the matrix, so the whole campaign costs one build per client per
# ceiling.
#
# The ethrex build clones upstream into a scratch directory and drives ethrex's own Dockerfile,
# so we inherit their build recipe instead of re-deriving it. Nothing outside that scratch
# directory and the Docker daemon is written.
set -euo pipefail

readonly HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly DEVNET_ROOT="$(cd "${HERE}/.." && pwd)"
readonly REPO_ROOT="$(cd "${DEVNET_ROOT}/../.." && pwd)"

readonly CAMPAIGN_CEILINGS=(100000 236285 300000 322800 500000)

ETHREX_REPO="${ETHREX_REPO:-https://github.com/lambdaclass/ethrex}"
ETHREX_REF="${ETHREX_REF:-main}"
WORK_DIR="${FRAME_TX_BUILD_DIR:-${TMPDIR:-/tmp}/frame-tx-devnet-build}"
MANIFEST="${DEVNET_ROOT}/images/build-manifest.json"

usage() {
  sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'
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

wants() { [[ -z "${only}" || "${only}" == "$1" ]]; }

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
  echo "==> building ${tag} from ${REPO_ROOT} @ ${commit}"
  docker build \
    -f "${DEVNET_ROOT}/images/nethermind/Dockerfile" \
    --build-arg "MAX_VERIFY_GAS=${ceiling}" \
    --build-arg "COMMIT_HASH=${commit}" \
    -t "${tag}" \
    "${REPO_ROOT}"
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
