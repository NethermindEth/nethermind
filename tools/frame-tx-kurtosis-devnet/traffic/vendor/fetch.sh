#!/usr/bin/env bash
# Fetches the third-party material the traffic generator builds on, pinned by commit and
# verified by checksum. Run by the traffic image build; also runnable locally for development.
#
#   traffic/vendor/fetch.sh [destination]    # default: the directory holding this script
#
# Why fetch rather than commit the files: soispoke/minimal-shielded-pool is the authoritative
# EIP-8141 encoder and the only source of real shielded-pool proving material, and it carries
# its own licence. Pinning by commit keeps runs reproducible without vendoring third-party
# source into the Nethermind tree.
#
# frametx.py is required. The shielded-pool modules are optional: without them the
# soispoke-groth16 role can still flood with the real verifier and real invalid proofs (the
# attack the campaign measures), but the valid-privacy-transaction probe is unavailable and
# says so rather than substituting a synthetic proof.
set -euo pipefail

readonly DEST="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"

readonly SOISPOKE_REPO="soispoke/minimal-shielded-pool"
# position-notes-v2, the profile the v2 campaign measures (235,800 declared). A commit rather
# than a branch, so a rebuild fetches the same bytes the checksum below was taken from.
readonly SOISPOKE_REF="${SOISPOKE_REF:-6dedda193bf09c9d80cd89b3dc23eccf580d1026}"

# Required file: path -> sha256 of the pinned revision. Set FRAME_TX_VENDOR_ALLOW_DRIFT=1 to
# accept a different revision (it will be reported), for instance when bumping the pin.
readonly REQUIRED_PATH="devnet/frametx.py"
readonly REQUIRED_SHA256="ac008480a576bae7543287f7488afb93c1b12c1cdf103f95e0f933b68823d459"

readonly OPTIONAL_PATHS=(
  "devnet/pool_frametx.py"
  "devnet/dispatcher.py"
  "devnet/deploy_config.json"
)

fetch() {
  local path="$1" out="$2"
  curl -fsSL --retry 3 --max-time 60 \
    "https://raw.githubusercontent.com/${SOISPOKE_REPO}/${SOISPOKE_REF}/${path}" -o "${out}"
}

mkdir -p "${DEST}/soispoke"

out="${DEST}/soispoke/$(basename "${REQUIRED_PATH}")"
if ! fetch "${REQUIRED_PATH}" "${out}"; then
  echo "error: could not fetch ${REQUIRED_PATH} from ${SOISPOKE_REPO}@${SOISPOKE_REF}" >&2
  exit 1
fi

if command -v sha256sum >/dev/null; then
  actual=$(sha256sum "${out}" | cut -d' ' -f1)
else
  actual=$(shasum -a 256 "${out}" | cut -d' ' -f1)
fi
if [[ "${actual}" != "${REQUIRED_SHA256}" ]]; then
  if [[ "${FRAME_TX_VENDOR_ALLOW_DRIFT:-0}" == "1" ]]; then
    echo "warning: ${REQUIRED_PATH} is ${actual}, pinned ${REQUIRED_SHA256}; accepted via FRAME_TX_VENDOR_ALLOW_DRIFT" >&2
  else
    echo "error: ${REQUIRED_PATH} checksum mismatch." >&2
    echo "       expected ${REQUIRED_SHA256}" >&2
    echo "       actual   ${actual}" >&2
    echo "       The encoder changed upstream. Review the frame-transaction envelope for a" >&2
    echo "       dialect change before re-pinning, then update REQUIRED_SHA256 here." >&2
    exit 1
  fi
fi

for path in "${OPTIONAL_PATHS[@]}"; do
  if fetch "${path}" "${DEST}/soispoke/$(basename "${path}")" 2>/dev/null; then
    echo "fetched optional ${path}"
  else
    echo "note: optional ${path} unavailable; the valid-privacy probe will report it as missing"
  fi
done

cat > "${DEST}/soispoke/PROVENANCE.txt" <<EOF
source=https://github.com/${SOISPOKE_REPO}
ref=${SOISPOKE_REF}
required=${REQUIRED_PATH} sha256=${actual}
fetched_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF

echo "vendored into ${DEST}/soispoke"
