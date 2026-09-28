#!/usr/bin/env bash
# Builds the client images one scenario ceiling needs, plus the traffic generator.
#
#   images/build.sh 235800                     # Nethermind and ethrex at that ceiling, and the generator
#   images/build.sh 235800 --only nethermind   # one of: nethermind, ethrex, traffic
#   images/build.sh --all                      # every predefined campaign ceiling
#
# Both clients carry the ceiling as a compile-time constant, so a ceiling change means an image
# change. Nethermind's runtime --TxPool.FrameTxMaxVerifyGas bounds admission only; ethrex has no
# runtime knob. Images are tagged by ceiling and reused across every scenario at that ceiling.
#
# Nethermind: the repository's own Dockerfile, built from `git archive` of NETHERMIND_REF
# (default HEAD) with images/nethermind/patch.sh applied. Uncommitted changes are never in the
# image, and the recorded commit is exactly what was built.
# ethrex: ETHREX_REF (default: a pinned frames-devnet-0 commit) fetched into a scratch directory,
# patched by images/ethrex/patch.sh, built with ethrex's own Dockerfile. The first ethrex build
# compiles Rust and takes 15 to 30 minutes.
set -euo pipefail

readonly HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly DEVNET_ROOT="$(cd "${HERE}/.." && pwd)"
readonly REPO_ROOT="$(cd "${DEVNET_ROOT}/../.." && pwd)"

readonly CAMPAIGN_CEILINGS=(100000 235800 250000 300000 400000 500000)

ETHREX_REPO="${ETHREX_REPO:-https://github.com/lambdaclass/ethrex}"
# frames-devnet-0 decodes the same frame envelope as Nethermind (limits list, nested fees); main
# still decodes the older one. Pinned by commit so a rebuild is the same client.
ETHREX_REF="${ETHREX_REF:-52c2e626c004852c35513ed64084c53dd94d6a97}"
# The Nethermind commit to build. Defaults to this checkout's HEAD.
NETHERMIND_REF="${NETHERMIND_REF:-HEAD}"
WORK_DIR="${FRAME_TX_BUILD_DIR:-${TMPDIR:-/tmp}/frame-tx-devnet-build}"
MANIFEST="${DEVNET_ROOT}/images/build-manifest.json"

usage() {
  sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
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
  commit=$(git -C "${REPO_ROOT}" rev-parse "${NETHERMIND_REF}^{commit}")
  if [[ "${NETHERMIND_REF}" == "HEAD" && -n "$(git -C "${REPO_ROOT}" status --porcelain -- src/Nethermind)" ]]; then
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
  if [[ ! -d "${checkout}/.git" ]]; then
    rm -rf "${checkout}"
    git init -q "${checkout}"
    git -C "${checkout}" remote add origin "${ETHREX_REPO}"
  fi
  # fetch rather than clone --branch, which cannot take a commit.
  git -C "${checkout}" fetch --depth 1 origin "${ETHREX_REF}"
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
    --label "org.nethermind.frame_tx.max_verify_gas=${ceiling}" \
    --label "org.opencontainers.image.revision=${sha}" \
    -t "${tag}" \
    "${checkout}"
  record "${MANIFEST}" "${tag}" "kind=ethrex" "max_verify_gas=${ceiling}" \
    "source_repo=${ETHREX_REPO}" "source_ref=${ETHREX_REF}" "source_commit=${sha}"
}

if wants traffic; then
  build_traffic
fi

# The +-expansion keeps bash 3.2 (macOS) from failing on an empty array under set -u.
for ceiling in ${ceilings[@]+"${ceilings[@]}"}; do
  if ! [[ "${ceiling}" =~ ^[1-9][0-9]*$ ]]; then
    echo "error: '${ceiling}' is not a positive integer ceiling" >&2
    exit 1
  fi
  wants nethermind && build_nethermind "${ceiling}"
  wants ethrex && build_ethrex "${ceiling}"
done

echo
echo "build manifest: ${MANIFEST}"
