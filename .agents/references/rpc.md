# RPC Module Context

Knowledge specific to Nethermind.JsonRpc, Nethermind.Facade, and RPC module implementations.

## State validation

- RPC methods executing against historical state must: (1) find header via `SearchForHeader`,
  (2) check `blockchainBridge.HasStateForBlock`. Missing header -> `ResourceNotFound`.
  Pruned state -> `ResourceUnavailable`. `TraceRpcModule` has 8 sites following this pattern.

## Parameter ordering

- JSON-RPC uses positional arrays, so parameter order is part of the wire protocol.
  New optional parameters must go at the END — inserting before existing optional params
  shifts positions and breaks callers using positional args.

## Simulation

- `header.Clone()` in RPC simulation paths must zero cumulative fields (`GasUsed`,
  `BlobGasUsed`, etc.). Without this, simulated transactions are constrained by the
  head block's consumption.

## L2 integration

- The global serializer uses `JsonNamingPolicy.CamelCase`. L2 interop protocols (op-node,
  Flashbots relay) expect snake_case. Every property on L2 RPC DTOs needs explicit
  `[JsonPropertyName("snake_name")]`.

## Logging

- `if (_logger.IsX) _logger.Y(...)` — the guard level must match the log method.
  40+ mismatches exist in the codebase. `IsTrace` guarding `Error()` hides errors.
  `IsError` guarding `Info()` suppresses info messages.
