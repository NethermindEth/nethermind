# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Starts the node, waits for JSON-RPC to report the expected chain ID, runs the given assertions,
# and checks that the node shuts down cleanly on SIGTERM.
{
  runCommand,
  curl,
  nethermind,
}:
{
  name,
  nodeArgs,
  chainId,
  rpcPort ? 8545,
  setup ? "",
  assertions ? "",
  extraInputs ? [ ],
}:
runCommand name
  {
    nativeBuildInputs = [
      curl
      nethermind
    ]
    ++ extraInputs;
    __darwinAllowLocalNetworking = true;
  }
  ''
    export HOME="$TMPDIR"
    cd "$TMPDIR"

    fail() {
      echo "FAIL: $1" >&2
      echo "--- last 100 lines of nethermind.log ---" >&2
      tail -n 100 nethermind.log >&2 || true
      exit 1
    }

    rpc() {
      curl -fsS -m "''${2:-30}" -X POST -H 'Content-Type: application/json' --data "$1" http://127.0.0.1:${toString rpcPort}
    }

    ${setup}

    nethermind ${nodeArgs} -dd "$TMPDIR/data" --JsonRpc.Port ${toString rpcPort} \
      > nethermind.log 2>&1 &
    pid=$!

    response=""
    for _ in $(seq 1 180); do
      kill -0 "$pid" 2>/dev/null || fail "node exited before JSON-RPC came up"
      if response=$(rpc '{"jsonrpc":"2.0","method":"eth_chainId","params":[],"id":1}' 5 2>/dev/null); then
        break
      fi
      sleep 1
    done
    [ -n "$response" ] || fail "JSON-RPC did not answer within 180s"
    echo "eth_chainId response: $response"
    echo "$response" | grep -qF '"result":"${chainId}"' || fail "unexpected eth_chainId response"

    ${assertions}

    kill -TERM "$pid" || fail "node was gone before the shutdown request"
    for _ in $(seq 1 120); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 1
    done
    kill -0 "$pid" 2>/dev/null && fail "node did not shut down within 120s of SIGTERM"
    status=0
    wait "$pid" || status=$?
    { [ "$status" -eq 130 ] || [ "$status" -eq 143 ]; } \
      || fail "expected exit code 130 or 143 after SIGTERM, got $status"

    touch "$out"
  ''
