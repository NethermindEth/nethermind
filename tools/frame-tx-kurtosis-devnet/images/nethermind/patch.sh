#!/usr/bin/env bash
# Applies the devnet-only changes Nethermind needs to take part in this benchmark. images/build.sh
# and check-patch.sh run it on a scratch export of a commit; it is never run against a working
# tree. It runs on the host, so it sticks to bash 3.2, grep -F and python3 to work on macOS too.
#
#   1. Eip8141Constants.MaxVerifyGas -> the scenario's ceiling. ITxPoolConfig.FrameTxMaxVerifyGas,
#      which the runner sets to the same value, bounds the declared validation gas at admission.
#      The simulated prefix is capped frame by frame at this compile-time constant
#      (TransactionProcessorBase.FrameTx.cs), so without it a prefix above 300_000 is cut short.
#
#   2. EIP-8141 activation. Upstream schedules EIP-8141 on its own `eip8141PrototypeTime` key,
#      which no genesis generator emits, so a stock build never activates it on a generated
#      devnet genesis. The frames devnet's generator writes the fork as bogotaTime, meaning
#      Amsterdam plus EIP-8141, and ethrex reads it that way. Upstream Nethermind reads
#      bogotaTime as its Bogota fork class (Amsterdam plus EIP-7805), which moves
#      engine_newPayload to V6 and has every payload rejected with -38005. So bogotaTime and
#      hezeTime are routed to EIP-8141 alone, as the upstream frames-devnet-0 deployment commit
#      3c210d3c does. HardforkLabels.ExpandAll reads the named-fork dictionary rather than the
#      property, so the mapping has to happen in the setter.
#
# Usage: patch.sh <source-root> <max-verify-gas>
set -euo pipefail

readonly SRC_ROOT="${1:?usage: patch.sh <source-root> <max-verify-gas>}"
readonly CEILING="${2:?usage: patch.sh <source-root> <max-verify-gas>}"

if ! [[ "${CEILING}" =~ ^[1-9][0-9]*$ ]]; then
  echo "error: max-verify-gas '${CEILING}' is not a positive integer" >&2
  exit 1
fi

readonly CONSTANTS="${SRC_ROOT}/Nethermind.Core/Eip8141Constants.cs"
readonly GENESIS="${SRC_ROOT}/Nethermind.Specs/ChainSpecStyle/Json/GethGenesisConfigJson.cs"

# Every edit below asserts its anchor is unique first: a silent no-op here produces an image
# that looks patched and measures the wrong ceiling.
assert_count() {
  local file="$1" pattern="$2" want="$3" what="$4"
  local got
  got=$(grep -cF -- "${pattern}" "${file}" || true)
  if [[ "${got}" != "${want}" ]]; then
    echo "error: expected ${want} occurrence(s) of ${what} in ${file}, found ${got}." >&2
    echo "       The anchor moved upstream; update images/nethermind/patch.sh." >&2
    exit 1
  fi
}

# Replaces the one occurrence of a string that assert_count has just proven unique. python3
# rather than sed: BSD sed reads -i's argument as a backup suffix and writes \n literally.
replace() {
  python3 - "$1" "$2" "$3" <<'PY'
import sys
path, old, new = sys.argv[1:4]
with open(path) as handle:
    text = handle.read()
with open(path, "w") as handle:
    handle.write(text.replace(old, new, 1))
PY
}

readonly STOCK_CONST='public const ulong MaxVerifyGas = 300_000;'
assert_count "${CONSTANTS}" "${STOCK_CONST}" 1 "the stock MaxVerifyGas constant"
replace "${CONSTANTS}" "${STOCK_CONST}" "public const ulong MaxVerifyGas = ${CEILING};"
assert_count "${CONSTANTS}" "public const ulong MaxVerifyGas = ${CEILING};" 1 "the patched MaxVerifyGas constant"

readonly STOCK_BOGOTA='    public ulong? BogotaTime { get => GetTime(); set => SetTime(value); }'
assert_count "${GENESIS}" "${STOCK_BOGOTA}" 1 "the stock BogotaTime property"
replace "${GENESIS}" "${STOCK_BOGOTA}" "$(printf '%s\n%s' \
  '    public ulong? BogotaTime { get => GetTime(); set => SetTime(value, "Eip8141Prototype"); }' \
  '    public ulong? HezeTime { get => GetTime(); set => SetTime(value, "Eip8141Prototype"); }')"
assert_count "${GENESIS}" 'public ulong? HezeTime' 1 "the added HezeTime property"
assert_count "${GENESIS}" 'set => SetTime(value, "Eip8141Prototype");' 2 "the EIP-8141 activation mapping"

cat <<EOF
Nethermind devnet patch applied:
  Eip8141Constants.MaxVerifyGas = ${CEILING}
  bogotaTime / hezeTime activate EIP-8141 alone (not EIP-7805)
EOF
