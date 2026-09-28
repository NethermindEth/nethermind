#!/usr/bin/env bash
# Raises ethrex's MAX_VERIFY_GAS to the scenario's ceiling, in a throwaway clone. Runs on the
# host, so it avoids GNU-only tools.
#
# ethrex hardcodes the ceiling as a `pub const` and exposes no CLI flag or config for it
# (crates/common/types/transaction.rs; see docs/eip-8141.md). Every scenario above 100_000 gas
# therefore needs its own ethrex image. The constant is public and used through the crate, so
# patching the declaration covers every enforcement site.
#
# Usage: patch.sh <ethrex-checkout> <max-verify-gas>
set -euo pipefail

readonly SRC="${1:?usage: patch.sh <ethrex-checkout> <max-verify-gas>}"
readonly CEILING="${2:?usage: patch.sh <ethrex-checkout> <max-verify-gas>}"

if ! [[ "${CEILING}" =~ ^[1-9][0-9]*$ ]]; then
  echo "error: max-verify-gas '${CEILING}' is not a positive integer" >&2
  exit 1
fi

readonly FILE="${SRC}/crates/common/types/transaction.rs"
readonly STOCK='pub const FRAME_TX_MAX_VERIFY_GAS: u64 = 100_000;'

if [[ ! -f "${FILE}" ]]; then
  echo "error: ${FILE} not found; ethrex's layout moved." >&2
  exit 1
fi

count=$(grep -cF -- "${STOCK}" "${FILE}" || true)
if [[ "${count}" != "1" ]]; then
  echo "error: expected 1 occurrence of the stock FRAME_TX_MAX_VERIFY_GAS constant in ${FILE}, found ${count}." >&2
  echo "       The anchor moved upstream; update images/ethrex/patch.sh." >&2
  exit 1
fi

# python3 rather than sed -i, which differs between GNU and BSD (macOS).
python3 - "${FILE}" "${STOCK}" "pub const FRAME_TX_MAX_VERIFY_GAS: u64 = ${CEILING};" <<'PY'
import sys
path, old, new = sys.argv[1:4]
with open(path) as handle:
    text = handle.read()
with open(path, "w") as handle:
    handle.write(text.replace(old, new, 1))
PY

patched=$(grep -cF -- "pub const FRAME_TX_MAX_VERIFY_GAS: u64 = ${CEILING};" "${FILE}" || true)
if [[ "${patched}" != "1" ]]; then
  echo "error: patch did not apply to ${FILE}" >&2
  exit 1
fi

echo "ethrex devnet patch applied: FRAME_TX_MAX_VERIFY_GAS = ${CEILING}"
