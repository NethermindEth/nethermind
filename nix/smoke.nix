# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Starts the packaged node peerless with the mainnet config and asserts that
# JSON-RPC answers with the mainnet chain id and that SIGINT shuts it down
# cleanly. Unlike the version check, this exercises chainspec parsing, genesis
# processing (with Init.GenesisHash validation), the bundled native libraries
# (rocksdb, secp256k1), plugin loading, and the dotnet-runtime wrapper.
#
# Readiness is defined as an answered RPC call, not a bound port: the listener
# accepts connections before the JSON-RPC handlers respond.
{
  runCommand,
  curl,
  nethermind,
}:
runCommand "nethermind-smoke-check"
  {
    nativeBuildInputs = [
      curl
      nethermind
    ];
    # The probe talks to loopback, which the darwin sandbox blocks by default.
    __darwinAllowLocalNetworking = true;
  }
  ''
    export HOME="$TMPDIR"
    cd "$TMPDIR"

    nethermind -c mainnet -dd "$TMPDIR/data" \
      --Init.DiscoveryEnabled false \
      --Init.PeerManagerEnabled false \
      > nethermind.log 2>&1 &
    pid=$!

    fail() {
      echo "FAIL: $1" >&2
      echo "--- last 100 lines of nethermind.log ---" >&2
      tail -n 100 nethermind.log >&2 || true
      exit 1
    }

    request='{"jsonrpc":"2.0","method":"eth_chainId","params":[],"id":1}'
    response=""
    for _ in $(seq 1 180); do
      kill -0 "$pid" 2>/dev/null || fail "node exited before JSON-RPC came up"
      if response=$(curl -fsS -m 5 -X POST -H 'Content-Type: application/json' \
          --data "$request" http://127.0.0.1:8545 2>/dev/null); then
        break
      fi
      sleep 1
    done
    [ -n "$response" ] || fail "JSON-RPC did not answer within 180s"
    echo "eth_chainId response: $response"
    echo "$response" | grep -qF '"result":"0x1"' || fail "unexpected eth_chainId response"

    kill -INT "$pid" || fail "node was gone before the shutdown request"
    for _ in $(seq 1 120); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 1
    done
    kill -0 "$pid" 2>/dev/null && fail "node did not shut down within 120s of SIGINT"
    status=0
    wait "$pid" || status=$?
    # Graceful SIGINT shutdown deliberately exits 130, see ExitCodes.SigInt.
    [ "$status" -eq 130 ] || fail "expected exit code 130 after SIGINT, got $status"

    touch "$out"
  ''
