# Nethermind.Mcp.Plugin

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server that gives AI agents and other MCP
clients read-only access to the node's chain data. It exposes a small, fixed set of tools (chain head, blocks,
transactions, receipts, balances, code, logs and `eth_call`) over the MCP Streamable HTTP transport.

The server runs on its own loopback-only listener, separate from the JSON-RPC and Engine API endpoints. Tools
read data through the node's own `eth` JSON-RPC module, so their results use the same encoding as JSON-RPC
(hex quantities, `0x`-prefixed data).

The plugin is built into the node and **disabled by default**.

## Security model

- **Loopback only.** The listener binds to a loopback IP address (`127.0.0.1` by default, or `::1`). Any other
  `Mcp.Host` value is rejected at startup. There is no remote mode in this version. To reach the server from
  another machine, use your own authenticated tunnel, such as SSH port forwarding.
- **Own port.** `Mcp.Port` must differ from every JSON-RPC, WebSocket, Engine API, additional RPC URL and metrics
  port. The node refuses to start if a port is shared.
- **Host validation.** Requests must carry a `Host` header of `127.0.0.1:<port>`, `localhost:<port>` or
  `[::1]:<port>`. Anything else gets HTTP 400. This blocks DNS-rebinding attacks from web pages.
- **Origin validation.** Requests without an `Origin` header are allowed. Requests with an `Origin` header that is
  not listed in `Mcp.AllowedOrigins` get HTTP 403. The server never sends CORS headers.
- **Optional bearer token.** When `Mcp.AuthTokenFile` is set, every request must send
  `Authorization: Bearer <token>`. Otherwise, the server returns HTTP 401 with `WWW-Authenticate: Bearer`. The
  token is the trimmed content of the file and must be at least 32 characters long. Without a token file, any
  local process can use the server.
- **Read-only tools.** No tool changes node state, submits transactions or signs anything.
  See [Not exposed](#not-exposed).
- **Bounded work.** Concurrency, time, request size, result size, log range and `call` gas are capped.
  See [Limits](#limits).
- Tokens and request bodies are never logged.

Set a token on any host where other users or untrusted local software can open loopback connections.

Generate a token:

```bash
openssl rand -hex 32 > mcp.token
chmod 600 mcp.token
```

> [!IMPORTANT]
> Do not reuse the Engine API JWT secret (`JsonRpc.JwtSecretFile`) as the MCP token. Anyone who has the MCP
> token could then also authenticate to the Engine API.

## Enabling

Command line:

```bash
nethermind -c mainnet --Mcp.Enabled true --Mcp.AuthTokenFile /path/to/mcp.token
```

Environment variables:

```bash
NETHERMIND_MCPCONFIG_ENABLED=true
NETHERMIND_MCPCONFIG_AUTHTOKENFILE=/path/to/mcp.token
```

Configuration file:

```json
{
  "Mcp": {
    "Enabled": true,
    "Port": 8555,
    "AuthTokenFile": "/path/to/mcp.token"
  }
}
```

On startup, the node logs a line like this:

```
MCP server is listening on http://127.0.0.1:8555/mcp (bearer token required)
```

If the configuration is invalid or the port cannot be bound, the node stops at startup with an error that names
the problem.

### Docker

Inside a container, loopback is the container's own network namespace. Publishing the port with `-p` does
not make the server reachable from the host, because nothing outside the container can connect to its loopback
address. Run MCP clients inside the container, or in a container that shares its network namespace
(`--network container:<nethermind-container>`).

## Configuration

| Key | Default | Description |
|---|---|---|
| `Mcp.Enabled` | `false` | Whether to start the MCP server. |
| `Mcp.Host` | `127.0.0.1` | The loopback IP address to bind to (`127.0.0.1` or `::1`). Host names and non-loopback addresses are rejected. |
| `Mcp.Port` | `8555` | The TCP port. The endpoint is `http://<Host>:<Port>/mcp`. |
| `Mcp.AuthTokenFile` | `null` | Path to the file holding the bearer token (trimmed, at least 32 characters). If unset, no authentication is required. |
| `Mcp.AllowedOrigins` | `[]` | Browser origins (`scheme://host[:port]`, for example `http://localhost:6274`) allowed to send requests. Comma-separated on the command line. |
| `Mcp.MaxRequestBodySize` | `262144` | The maximum HTTP request body size, in bytes. Larger requests get HTTP 413. |
| `Mcp.MaxConcurrentToolCalls` | `4` | The maximum number of tool calls running at once. Further calls fail immediately with `resource_exhausted`. |
| `Mcp.ToolTimeout` | `10000` | The wall-clock limit for one tool call, in milliseconds. Must not exceed `JsonRpc.Timeout`. |
| `Mcp.MaxResultSize` | `4194304` | The maximum size of a serialized tool result, in bytes. |
| `Mcp.MaxLogBlockRange` | `1000` | The maximum number of blocks, inclusive, one `get_logs` call may span. |
| `Mcp.MaxLogs` | `10000` | The maximum number of logs one `get_logs` call may return. |
| `Mcp.MaxCallGas` | `50000000` | The maximum gas one `call` may use. Must not exceed `JsonRpc.GasCap` when that is set. |
| `Mcp.MaxCallDataSize` | `131072` | The maximum `call` input data size, in bytes. |

All numeric limits must be positive.

## Connecting clients

The examples assume the default endpoint `http://127.0.0.1:8555/mcp` and a token in `mcp.token`. If no token is
configured, leave out the `Authorization` header.

The server uses stateless Streamable HTTP at `/mcp` only. There is no stdio transport, no legacy SSE endpoint and
no session. Every other path returns HTTP 404. The server accepts MCP protocol versions `2024-11-05` through
`2026-07-28`.

### Claude Code

```bash
claude mcp add --transport http nethermind http://127.0.0.1:8555/mcp \
  --header "Authorization: Bearer $(cat mcp.token)"
```

### Generic JSON client configuration

Most MCP clients that support remote HTTP servers accept a configuration like this:

```json
{
  "mcpServers": {
    "nethermind": {
      "type": "http",
      "url": "http://127.0.0.1:8555/mcp",
      "headers": {
        "Authorization": "Bearer <contents of mcp.token>"
      }
    }
  }
}
```

The exact key names vary by client. Check your client's documentation.

### MCP Inspector

```bash
npx @modelcontextprotocol/inspector
```

In the Inspector UI:

1. Set **Transport Type** to **Streamable HTTP** and **URL** to `http://127.0.0.1:8555/mcp`.
2. Keep **Connection Type** set to **Via Proxy**. A direct browser connection cannot read responses, because the
   server sends no CORS headers.
3. Add an `Authorization` header with the value `Bearer <token>`.

If the Inspector gets HTTP 403, it is sending an `Origin` header. Add that origin to `Mcp.AllowedOrigins`, for
example `--Mcp.AllowedOrigins http://localhost:6274`.

### curl

With protocol version `2026-07-28`, there is no `initialize` handshake. Every request carries the protocol version
in both the `MCP-Protocol-Version` header and the `params._meta` object. The request also names its method in the
`Mcp-Method` header, and a `tools/call` request names its tool in the `Mcp-Name` header.

```bash
MCP_URL=http://127.0.0.1:8555/mcp
MCP_TOKEN=$(cat mcp.token)
META='"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"curl","version":"1.0.0"},"io.modelcontextprotocol/clientCapabilities":{}}'

# Usage: mcp <method> <params members> [tool name]
mcp() {
  local extra=()
  [ -n "$3" ] && extra=(-H "Mcp-Name: $3")
  curl -sS "$MCP_URL" \
    -H "Authorization: Bearer $MCP_TOKEN" \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -H 'MCP-Protocol-Version: 2026-07-28' \
    -H "Mcp-Method: $1" \
    "${extra[@]}" \
    -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"$1\",\"params\":{$2}}"
}

# Server identity, capabilities and supported protocol versions
mcp server/discover "$META"

# Tool list with input schemas
mcp tools/list "$META"

# Tool calls
mcp tools/call "\"name\":\"chain_info\",\"arguments\":{},$META" chain_info
mcp tools/call "\"name\":\"get_block\",\"arguments\":{\"block\":\"finalized\"},$META" get_block
```

Clients on earlier protocol versions use the `initialize` handshake. Because the server is stateless, later
requests need only the `MCP-Protocol-Version` header and no session ID:

```bash
curl -sS "$MCP_URL" \
  -H "Authorization: Bearer $MCP_TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"curl","version":"1.0.0"}}}'

curl -sS "$MCP_URL" \
  -H "Authorization: Bearer $MCP_TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2025-11-25' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
```

A response can come back as a server-sent event stream. The JSON-RPC message is then on the `data:` line. To
extract it, pipe the output through `sed -n 's/^data: //p' | jq`.

### C# (official MCP SDK)

Reference the `ModelContextProtocol.Core` package (2.2.0 or later):

```csharp
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

string token = File.ReadAllText("mcp.token").Trim();

await using HttpClientTransport transport = new(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://127.0.0.1:8555/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
});

await using McpClient client = await McpClient.CreateAsync(transport);
Console.WriteLine($"Connected to {client.ServerInfo.Name} {client.ServerInfo.Version}");

foreach (McpClientTool tool in await client.ListToolsAsync())
{
    Console.WriteLine($"{tool.Name}: {tool.Description}");
}

CallToolResult head = await client.CallToolAsync("chain_info");
Console.WriteLine(head.StructuredContent?.ToString());

CallToolResult balance = await client.CallToolAsync(
    "get_balance",
    new Dictionary<string, object?>
    {
        ["address"] = "0x00000000219ab540356cBB839Cbe05303d7705Fa",
        ["block"] = "latest",
    });
Console.WriteLine($"{(balance.IsError == true ? "error" : "ok")}: {balance.StructuredContent}");
```

## Tools

All tools are annotated as read-only, non-destructive, idempotent and closed-world.

A **block selector** is one of: `latest`, `earliest`, `safe`, `finalized`, a block number (`0x`-prefixed hex or
decimal), or a block hash (`0x` followed by 64 hex characters). `pending` is not supported.

| Tool | Inputs | Output (`result`) | Notes |
|---|---|---|---|
| `chain_info` | none | `{ chainId, headNumber, headHash }` | Hex-encoded chain ID, head block number and head block hash. |
| `get_block` | `block` (selector), `fullTransactions` (bool, default `false`) | The block, as `eth_getBlockByNumber` or `eth_getBlockByHash` returns it | Returns full transaction objects when `fullTransactions` is `true`, otherwise transaction hashes. |
| `get_transaction` | `hash` | The transaction, as `eth_getTransactionByHash` returns it | |
| `get_transaction_receipt` | `hash` | The receipt, as `eth_getTransactionReceipt` returns it | |
| `get_balance` | `address`, `block` (selector, default `latest`) | The balance in wei, as a hex quantity | Needs state for the block. |
| `get_code` | `address`, `block` (selector, default `latest`) | The contract bytecode, as hex data | Needs state for the block. |
| `get_logs` | `fromBlock`, `toBlock`, `address` (optional list), `topics` (optional; each position is a topic, a list of topics or `null`) | A list of logs, as `eth_getLogs` returns them | The range may span at most `Mcp.MaxLogBlockRange` blocks and return at most `Mcp.MaxLogs` logs. At most 4 topic positions. |
| `call` | `to`, `data`, `gas` (required), `from`, `value`, `block` (selector, default `latest`) | The return data, as hex | Runs `eth_call`. You must set `gas`, and it may not exceed `Mcp.MaxCallGas` or `JsonRpc.GasCap`. `data` may be at most `Mcp.MaxCallDataSize` bytes. Nothing is persisted. |

A successful call returns `structuredContent` of the form `{ "result": ... }`, along with a text content block
that holds the same JSON.

## Errors

A failed tool call returns `isError: true` with `structuredContent` of the form
`{ "error": { "code": "...", "message": "...", "data": ... } }`. The `data` field is optional.

| Code | Meaning |
|---|---|
| `invalid_input` | An argument is missing or malformed. Examples: a bad block selector, an address that is not 20 bytes, missing `gas`. |
| `not_found` | The requested block, transaction or receipt does not exist. |
| `execution_reverted` | The `call` reverted. `data` holds the revert data as hex. |
| `resource_exhausted` | A limit was reached: too many concurrent calls, result too large, log range or log count too large, `call` gas or data above its cap, or no RPC module free. Retry later or narrow the request. |
| `timeout` | The call did not finish within `Mcp.ToolTimeout`. |
| `unavailable` | The node cannot answer yet or no longer can. Examples: state for the block is pruned, or the node is still syncing. |
| `internal_error` | An unexpected failure. Details go to the node log only. Clients never see stack traces. |

Protocol-level problems, such as an unknown tool or a malformed JSON-RPC message, are reported as standard
JSON-RPC errors. HTTP-level rejections use status codes: 400 (bad `Host`), 401 (missing or wrong token),
403 (disallowed `Origin`), 404 (path other than `/mcp`) and 413 (body too large).

## Limits

- **Concurrency:** `Mcp.MaxConcurrentToolCalls` tool calls at once. Extra calls fail fast. They are not queued.
- **Time:** each tool call is cancelled after `Mcp.ToolTimeout` ms. A client that cancels a request also cancels
  its tool call.
- **Request size:** `Mcp.MaxRequestBodySize` bytes per HTTP request.
- **Result size:** `Mcp.MaxResultSize` bytes of serialized result per call.
- **Logs:** `Mcp.MaxLogBlockRange` blocks and `Mcp.MaxLogs` results per `get_logs` call, and at most 4 topic positions.
- **Calls:** `gas` is required and capped by `Mcp.MaxCallGas` and `JsonRpc.GasCap`. Input data is capped by
  `Mcp.MaxCallDataSize`.

## Not exposed

The following are deliberately left out and are not reachable through this server:

- Arbitrary JSON-RPC pass-through. Only the tools listed above exist.
- Transaction submission (`eth_sendRawTransaction` and similar) and `pending` block or transaction pool data.
- Wallets, accounts, key management and signing.
- The Engine API.
- Admin, debug, trace, peer and network management methods.
- Filesystem access, including configuration, logs, keys and the database.
- Remote (non-loopback) access.

## Building the plugin

```bash
# From the repository root
cd src/Nethermind
dotnet build Nethermind.Mcp.Plugin/Nethermind.Mcp.Plugin.csproj -c Release
```
