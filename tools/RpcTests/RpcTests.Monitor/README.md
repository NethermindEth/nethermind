# RpcTests.Monitor

Continuously monitors an Ethereum RPC node by conditionally replaying parameterized test cases,
comparing responses with the reference node, and reporting mismatches.

Test files follow a format similar to [Erigon rpc-tests](https://github.com/erigontech/rpc-tests/), but can be parameterized.

## Options

| Option | Short | Default | Description                                                                  |
|--------|-------|---------|------------------------------------------------------------------------------|
| `--target` | `-t` | `http://localhost:8545` | Node under test (HTTP URL)                                                   |
| `--reference` | `-r` | — | Reference node for comparison. Required when tests have no static `response` |
| `--tests` | `-g` | `mainnet/**/*` | Glob pattern(s) for test files under `tests/`, repeatable                    |
| `--parallelism` | `-p` | `4` | Number of concurrent test workers per block                                  |
| `--report-at` | — | — | UTC time of day at which to report execution statistics (e.g. `12:00:00`)    |

## Examples

Monitor mainnet node against a Geth reference:

```bash
export RPC_MONITOR_BOT_TOKEN="xoxb-..."
export RPC_MONITOR_CHANNEL_ID="C1234567890"
dotnet run -- -t http://localhost:8545 -r http://geth-archive:8545
```

Narrow to specific method directories and increase parallelism:

```bash
dotnet run -- \
  -t localhost:8545 \
  -r geth-archive:8545 \
  -g "mainnet/eth_call/*" \
  -g "mainnet/eth_getLogs/*" \
  -p 8
```

Use static expected responses (no reference node needed):

```bash
export RPC_MONITOR_BOT_TOKEN="xoxb-..."
export RPC_MONITOR_CHANNEL_ID="C1234567890"
dotnet run -- -t localhost:8545 -g "mainnet/eth_call/*"
```

Report statistics at noon UTC daily:

```bash
export RPC_MONITOR_BOT_TOKEN="xoxb-..."
export RPC_MONITOR_CHANNEL_ID="C1234567890"
dotnet run -- -t node.example.com -r reference.example.com --report-at 12:00:00
```

## Test format
Example:
```json
[
  {
    "run": "EveryBlocks(5)",
    "test": {
      "description": "WETH transfers to Uniswap V3"
    },
    "request": {
      "jsonrpc": "2.0",
      "method": "eth_getLogs",
      "params": [{
        "fromBlock": "{{Hex(RecentBlock - 9)}}",
        "toBlock": "{{Hex(RecentBlock)}}",
        "address": ["0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2", "0xdAC17F958D2ee523a2206206994597C13D831ec7"],
        "topics": [
          "{{Topic.Transfer}}",
          null,
          "0x0000000000000000000000003fc91a3afd70395cd496c647d5a6cc9d4b2b7fad"
        ]
      }],
      "id": "{{Request.Number}}"
    }
  }
]
```

Each test can have the following fields:
- `run` (required): dynamic condition on whether test should be run;
- `request` (required): parameterized request JSON;
- `response`: to validate against a fixed expected value instead of using a reference node;
- `test` (optional): test metadata.

Some of them support custom C# expressions using [DynamicExpresso](https://github.com/dynamicexpresso/DynamicExpresso/):
- `run` is always assumed to be a boolean expression, evaluated once per new head;
- `request` and `response` are interpreted as JSON with support for parameterized properties (or array elements) –
   these are strings denoted as `{{ expression }}`.

[TestContext](./TestContext.cs) provides helper methods and properties for common patterns
available to be called directly (like `EveryBlocks`, `RecentBlock`, `Hex(n)`, etc.)

## Execution
The monitor subscribes to the new block events from the target node via WebSocket (`eth_subscribe("newHeads")`).

On each block it filters tests to run (evaluating `run` expression),
sends the requests to the target and optionally a reference node,
compares responses, and notifies on any discrepancy.

## Notification options
<img width="1022" height="168" alt="image" src="https://github.com/user-attachments/assets/5abf1004-7aeb-4c43-994a-3baf16484fe2" />

Only Slack is supported for now. Configure via environment variables:
- `RPC_MONITOR_BOT_TOKEN` + `RPC_MONITOR_CHANNEL_ID` — post messages to a Slack channel via a bot user, uploads responses as files.

Slack notifications are rate-limited to avoid spamming in case of consistent test/node/app failures.
