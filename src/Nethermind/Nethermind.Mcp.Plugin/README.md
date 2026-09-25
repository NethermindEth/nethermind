# Nethermind.Mcp.Plugin

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server built into Nethermind. It lets an AI agent
(Claude Code, Claude Desktop, Cursor, MCP Inspector or your own SDK client) ask your node questions in plain terms:
what a transaction did, what an address holds, what gas costs right now, whether the node is healthy.

It is **read-only** and **disabled by default**. It will never:

- submit, sign or broadcast transactions, or hold keys or wallets;
- expose the Engine API, admin, peer or network management, or the filesystem;
- pass arbitrary JSON-RPC through. Only the [24 tools](#tools) below exist.

Supported networks: Ethereum mainnet, Sepolia, Hoodi, Gnosis and Chiado (Holesky is also recognized by chain ID).
The server adapts to the chain, so amounts are labelled xDAI on Gnosis and Chiado and ETH elsewhere.

**Contents:** [Quick start](#quick-start) · [Remote nodes](#reaching-a-node-that-isnt-on-your-machine) ·
[Tools](#tools) · [Resources and prompts](#resources-and-prompts) · [Output](#output-conventions) ·
[Node types](#node-types-and-data-availability) · [Configuration](#configuration-reference) ·
[Security](#security-model) · [Troubleshooting](#troubleshooting)

## Quick start

### 1. Enable the server

```bash
nethermind -c mainnet --Mcp.Enabled=true
```

The endpoint is `http://127.0.0.1:8555/mcp`. At startup the node logs:

```
MCP server listening on http://127.0.0.1:8555/mcp (loopback, no authentication)
```

Without a token, any process on the machine can use the server. If other users or untrusted software run on the
host, add a bearer token:

```bash
openssl rand -hex 32 > mcp.token
chmod 600 mcp.token
nethermind -c mainnet --Mcp.Enabled=true --Mcp.AuthTokenFile=/path/to/mcp.token
```

The log line then ends with `(loopback, bearer auth)`. The token is the trimmed file content, at least 32 characters.

> [!IMPORTANT]
> Don't reuse the Engine API JWT secret (`JsonRpc.JwtSecretFile`) as the MCP token. Anyone holding the MCP token
> could then authenticate to the Engine API too.

The same settings work as environment variables or in a config file:

```bash
NETHERMIND_MCPCONFIG_ENABLED=true
NETHERMIND_MCPCONFIG_AUTHTOKENFILE=/path/to/mcp.token
```

```json
{
  "Mcp": {
    "Enabled": true,
    "AuthTokenFile": "/path/to/mcp.token"
  }
}
```

If the configuration is invalid or the port can't be bound, the node stops at startup with an error that names the
problem.

### 2. Connect a client

The server speaks stateless Streamable HTTP at `/mcp` only. There is no stdio transport, no legacy SSE endpoint and
no session. It accepts MCP protocol versions `2024-11-05` through `2026-07-28`.

#### Claude Code

```bash
# No token
claude mcp add --transport http nethermind http://127.0.0.1:8555/mcp

# With a token
claude mcp add --transport http nethermind http://127.0.0.1:8555/mcp \
  --header "Authorization: Bearer $(cat mcp.token)"
```

Check with `claude mcp list`, or `/mcp` inside a session. Add `--scope user` to make the server available in every
project.

#### Cursor and other JSON-configured clients

Most clients that support remote HTTP servers accept a configuration like this one (Cursor reads it from
`~/.cursor/mcp.json` or `.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "nethermind": {
      "url": "http://127.0.0.1:8555/mcp",
      "headers": {
        "Authorization": "Bearer <contents of mcp.token>"
      }
    }
  }
}
```

Key names vary by client (some want `"type": "http"`, VS Code uses `"servers"`), so check your client's
documentation. Leave out `headers` if you didn't set a token.

#### Claude Desktop

Claude Desktop's config file (`claude_desktop_config.json`) launches local stdio servers. To reach an HTTP server
on loopback, a common approach is the community `mcp-remote` bridge (requires Node.js):

```json
{
  "mcpServers": {
    "nethermind": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:8555/mcp", "--header", "Authorization:${AUTH_HEADER}"],
      "env": { "AUTH_HEADER": "Bearer <contents of mcp.token>" }
    }
  }
}
```

`mcp-remote` is a third-party package. Check its README for current flags. The environment variable avoids problems
with spaces in arguments on some platforms. Restart Claude Desktop after editing the file.

#### MCP Inspector

```bash
npx @modelcontextprotocol/inspector
```

1. Set **Transport Type** to **Streamable HTTP** and **URL** to `http://127.0.0.1:8555/mcp`.
2. Keep **Connection Type** set to **Via Proxy**. A direct browser connection can't read responses, because the
   server never sends CORS headers.
3. If you set a token, add an `Authorization` header with the value `Bearer <token>`.

Browser-based clients send an `Origin` header, and the server rejects any origin not listed in
`Mcp.AllowedOrigins` with HTTP 403. If the Inspector gets a 403, allow its origin:
`--Mcp.AllowedOrigins=http://localhost:6274`.

#### curl

With protocol version `2026-07-28` there is no `initialize` handshake. Every request carries the protocol version in
both the `MCP-Protocol-Version` header and `params._meta`, names its method in the `Mcp-Method` header, and a
`tools/call` request names its tool in the `Mcp-Name` header.

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

# Tool list with input and output schemas
mcp tools/list "$META"

# Tool calls
mcp tools/call "\"name\":\"chain_info\",\"arguments\":{},$META" chain_info
mcp tools/call "\"name\":\"node_status\",\"arguments\":{},$META" node_status
mcp tools/call "\"name\":\"get_block\",\"arguments\":{\"block\":\"finalized\"},$META" get_block
```

Clients on earlier protocol versions use the `initialize` handshake. Because the server is stateless, later requests
need only the `MCP-Protocol-Version` header and no session ID. This block reuses `MCP_URL` and `MCP_TOKEN` from the
block above:

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

Without a token, drop the `Authorization` header. A response can come back as a server-sent event stream, with the
JSON-RPC message on the `data:` line. To extract it, pipe the output through `sed -n 's/^data: //p' | jq`.

#### C# (official MCP SDK)

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

// {"result":{"balance":"0x...","balanceFormatted":"...","symbol":"ETH","decimals":18}}
CallToolResult balance = await client.CallToolAsync(
    "get_balance",
    new Dictionary<string, object?>
    {
        ["address"] = "0x00000000219ab540356cBB839Cbe05303d7705Fa",
        ["block"] = "latest",
    });
// Errors carry no structuredContent: read the JSON text instead.
Console.WriteLine(balance.IsError == true ? ((TextContentBlock)balance.Content[0]).Text : balance.StructuredContent?.ToString());
```

## Reaching a node that isn't on your machine

By default the server listens on loopback, so only processes on the node's own machine (or network namespace) can
reach it. Pick one of these options, roughly in order of preference.

> [!NOTE]
> The `Host` header must name the port the server listens on. If you forward a *different* local port (say
> `-L 9555:127.0.0.1:8555`), clients send `Host: 127.0.0.1:9555` and get HTTP 400. Either keep the same port on
> both ends, or allow the forwarded name: `--Mcp.AllowedHosts=localhost:9555,127.0.0.1:9555`.

### (a) SSH tunnel (recommended)

Leave the node on its loopback default and forward the port from your workstation:

```bash
ssh -N -L 8555:127.0.0.1:8555 user@node
```

Then connect your client to `http://127.0.0.1:8555/mcp` as if the node were local. SSH provides encryption and
authentication, so this needs no TLS setup on the node. Setting a token is still a good idea on shared hosts.

### (b) Docker

Inside a container, `127.0.0.1` is the container's own loopback. Publishing the port (`-p 8555:8555`) doesn't help
when the server listens on loopback, because nothing outside the container can connect to that address. You have
three options:

1. **Host networking** (Linux). The container shares the host's network, so the loopback default works unchanged,
   and an SSH tunnel to the host works as in (a):

   ```yaml
   services:
     nethermind:
       image: nethermind/nethermind:latest
       network_mode: host
       command:
         - --config=mainnet
         - --Mcp.Enabled=true
   ```

2. **Remote mode, published only on the host's loopback.** The server binds `0.0.0.0` inside the container, which
   turns on [remote mode](#e-remote-mode) (HTTPS, token and `AllowedHosts` are required). Docker publishes the port
   on the host's `127.0.0.1` only, so it's still not exposed to the network. Reach it from another machine with an
   SSH tunnel to the host:

   ```yaml
   services:
     nethermind:
       image: nethermind/nethermind:latest
       command:
         - --config=mainnet
         - --Mcp.Enabled=true
         - --Mcp.Host=0.0.0.0
         - --Mcp.AuthTokenFile=/secrets/mcp.token
         - --Mcp.TlsCertificatePath=/secrets/mcp.crt
         - --Mcp.TlsCertificateKeyPath=/secrets/mcp.key
         # The Host header values clients will send through the published port
         - --Mcp.AllowedHosts=localhost:8555,127.0.0.1:8555
       ports:
         - "127.0.0.1:8555:8555"
       volumes:
         - ./secrets:/secrets:ro
   ```

   The files must be readable by the user the container runs as. The client URL is `https://localhost:8555/mcp`
   (see [trusting a self-signed certificate](#trusting-a-self-signed-certificate)).

3. **Run the client in the container's network namespace** (`--network container:<nethermind-container>`).

### (c) Sedge

[Sedge](https://github.com/NethermindEth/sedge) generates a `docker-compose.yml` (by default in `sedge-data/`) where
the Nethermind container is the `execution` service on a bridge network. Because the consensus client reaches it by
service name, host networking isn't a good fit here. Use option (b) 2 instead: edit the `execution` service and add
the flags to its `command:` list, the port mapping to `ports:`, and a volume for the secrets:

```yaml
services:
  execution:
    # ...generated settings...
    command:
      # ...generated flags...
      - --Mcp.Enabled=true
      - --Mcp.Host=0.0.0.0
      - --Mcp.AuthTokenFile=/secrets/mcp.token
      - --Mcp.TlsCertificatePath=/secrets/mcp.crt
      - --Mcp.TlsCertificateKeyPath=/secrets/mcp.key
      - --Mcp.AllowedHosts=localhost:8555,127.0.0.1:8555
    ports:
      # ...generated ports...
      - "127.0.0.1:8555:8555"
    volumes:
      # ...generated volumes...
      - ./secrets:/secrets:ro
```

Then run `docker compose up -d execution` from the Sedge data directory. Running `sedge generate` again overwrites the
file, so keep a copy of your edits. Sedge also has an `--el-extra-flag` option for adding execution-client flags at
generation time (see `sedge generate --help`), but you still need to add the port and volume yourself.

### (d) DappNode

Untested. The Nethermind package on DappNode takes extra command-line flags through its `EXTRA_OPTS` environment
variable (package settings in the DappNode UI). The package runs in DappNode's Docker network, so the server has to
use remote mode (`--Mcp.Host=0.0.0.0` with a certificate, token and `AllowedHosts` set to the name you'll connect
with), and the certificate, key and token files have to be placed where the container can read them. Reach it over
DappNode's VPN (WireGuard or OpenVPN) rather than exposing the port. Check the package's documentation for the exact
variable name, file locations and internal host name before relying on this.

### (e) Remote mode

Setting `Mcp.Host` to any non-loopback IP address, including `0.0.0.0` or `::`, turns on remote mode. The node
refuses to start unless all of these hold:

| Requirement | Setting | What the validator checks |
|---|---|---|
| IP literal | `Mcp.Host` | An IP address, not a host name. Loopback (`127.0.0.1`, `::1`) is local mode, anything else is remote mode. |
| HTTPS | `Mcp.TlsCertificatePath` and `Mcp.TlsCertificateKeyPath` | Both set (they must always be set together). A PEM certificate (extra certificates in the file are sent as the chain) and its matching unencrypted PEM private key (PKCS#8, PKCS#1 or SEC1). Plain HTTP is never served on a non-loopback address. |
| Bearer token | `Mcp.AuthTokenFile` | Set, readable, and holding at least 32 characters after trimming. |
| Allowed hosts | `Mcp.AllowedHosts` | At least one entry. |
| Own port | `Mcp.Port` | Different from `JsonRpc.Port`, `JsonRpc.WebSocketsPort`, `JsonRpc.EnginePort`, every `JsonRpc.AdditionalRpcUrls` port, and `Metrics.ExposePort` when metrics are on. This applies in local mode too. |

A certificate that has expired or expires within 14 days only logs a warning, and so does one that isn't valid yet.
The listener uses TLS 1.2 or 1.3 and doesn't request client certificates. On startup, remote mode logs an extra
warning that node data is being served to the network.

#### Generating a self-signed certificate

```bash
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 365 \
  -keyout mcp.key -out mcp.crt -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
chmod 600 mcp.key
```

Put every name and IP address clients will use in `subjectAltName` (for example `DNS:node.example.com`). A
certificate from your own CA or a public CA works the same way. HTTPS on a loopback `Host` is also supported: set
the two certificate paths without changing `Mcp.Host`.

#### Trusting a self-signed certificate

Clients reject a self-signed certificate until you tell them to trust it:

- curl: `curl --cacert mcp.crt https://localhost:8555/mcp ...`
- Node.js-based clients (Claude Code, MCP Inspector, `mcp-remote`) generally honour `NODE_EXTRA_CA_CERTS=/path/to/mcp.crt`
  in their environment.
- Otherwise, add the certificate to your operating system's trust store.

#### AllowedHosts semantics

- Entries are `host` or `host:port`: a DNS name, an IPv4 address, or an IPv6 address in brackets
  (`[2001:db8::1]:8555`). No scheme, path or wildcards. DNS names match case-insensitively.
- An entry without a port matches any port. A `Host` header without a port is compared as port 443 over HTTPS
  (80 over plain HTTP).
- **Loopback mode:** entries are accepted *in addition to* `127.0.0.1:<port>`, `localhost:<port>` and
  `[::1]:<port>`.
- **Remote mode:** *only* these entries are accepted, plus the bound IP address itself when it isn't a wildcard.
  With `0.0.0.0`, list every name clients use, including `localhost:8555` if you publish the port on the host's
  loopback as in option (b) 2.

#### Failed-authentication throttle

In remote mode, a client IP address (for IPv6, its /64) that fails authentication 10 times within 60 seconds gets
HTTP 429 with `Retry-After: 60` for further failing requests until the oldest failure leaves the window. A request
with the valid token is always accepted, so failures from a shared address can't lock out a client that holds the
token. At most 4,096 addresses are tracked; beyond that, failures are only counted in a shared bucket that never
blocks anyone. Rejected requests (400, 401, 403, 413, 429) get `Connection: close`. The throttle is off on loopback,
where every client shares one address. Behind a reverse proxy (or Docker port publishing), the server sees the
proxy's address, so all clients share one IP for the throttle.

> [!WARNING]
> Remote mode makes the server reachable by anyone who can reach the port and holds the token. Prefer an SSH tunnel
> or a VPN (WireGuard, Tailscale), and if you do use remote mode, firewall the port to trusted clients.

## Tools

Every tool is annotated read-only, non-destructive, idempotent and closed-world, and declares an output schema. Tool
descriptions include this node's actual limits, so an agent can plan within them.

**Block selectors** (the `block`, `fromBlock` and `toBlock` arguments): `latest`, `earliest`, `safe`, `finalized`, a
block number as decimal (`"19553778"`) or hex (`"0x12a05f2"`), or a 32-byte block hash. `pending` isn't supported.
**Addresses** are `0x` plus 40 hex characters in any letter case (an uppercase `0X` prefix is accepted too).
**Amounts** passed in (`value`, `gas`) are decimal or `0x` hex strings.

**Lenient arguments.** Agents often send slightly wrong JSON, so arguments are normalised before binding: a JSON
number or boolean where a string is expected becomes that string (`"block": 19553778` works), a single value where an
array is expected is wrapped (`"address": "0x…"` for `get_logs`, `"tokens": "0x…"` for `token_balances`), a string
holding a JSON array or object is parsed (`args`, `calls`, `topics`, `stateOverrides`), `"true"`/`"false"` strings
become booleans, and an explicit `null` for an optional argument counts as omitted. `decode_logs` accepts `hash` for
`txHash`. Anything that still doesn't fit fails with `invalid_input` naming the argument and the expected type,
never with a generic SDK error.

**LLM-sized defaults.** Clients such as Claude Code cap a tool result at roughly 25k tokens, so the tools that can
return a lot default to small pages and say in their descriptions how to get more: `get_logs` returns at most 100 logs
and about 128 KB per page (`limit` up to `Mcp.MaxLogs`, `maxBytes` up to three quarters of `Mcp.MaxResultSize`),
`get_block_receipts` 20 receipts (`limit` up to 1,000), `trace_transaction` 300 frames (`maxFrames` up to
`Mcp.MaxTraceCalls`), and `get_block` with `fullTransactions` the first 50 transactions (`transactionOffset` and
`transactionLimit` up to 1,000 page through the rest).

| Tool | Answers | Key arguments |
|---|---|---|
| **Start here** | | |
| `node_status` | Is my node synced and healthy, and which blocks can it serve? | none |
| `chain_info` | Which network is this, and what's the head block? | none |
| **Transactions and blocks** | | |
| `explain_transaction` | What did this transaction do, and why did it fail? | `hash` |
| `trace_transaction` | Which internal calls did it make, and where did it revert? | `hash`, `maxDepth` (0 to 24), `maxFrames` (default 300), `includeInput` |
| `simulate_transaction` | What would happen if I sent this (or this sequence)? | `to`, `data`, `from`, `value`, `gas`, `block`, `stateOverrides`, `calls` (up to 8) |
| `block_summary` | What happened in this block? | `block` |
| `get_transaction` | Raw transaction (`eth_getTransactionByHash`) | `hash` |
| `get_transaction_receipt` | Raw receipt (`eth_getTransactionReceipt`) | `hash` |
| `get_block` | Raw block (`eth_getBlockBy*`) | `block`, `fullTransactions`, `transactionOffset`, `transactionLimit` (1 to 1000, default 50) |
| `get_block_receipts` | Raw receipts of a block, paged | `block`, `offset`, `limit` (1 to 1000, default 20) |
| **Addresses, tokens and names** | | |
| `lookup_address` | What is this address: EOA, contract, token, proxy, ENS name? | `address`, `block` |
| `token_balances` | What does this address hold? | `owner`, `tokens` (up to 50; default: this chain's well-known tokens), `block` |
| `token_info` | What is this token: name, symbol, decimals, supply, standard, proxy? | `token`, `block` |
| `resolve_ens` | Which address is `vitalik.eth`? (mainnet, Sepolia and Holesky) | `name`, `block` |
| `get_balance` | Native balance (ETH or xDAI), raw and formatted | `address`, `block` |
| `get_code` | Bytecode at an address (`0x` means no code) | `address`, `block` |
| **Gas and fees** | | |
| `fee_estimate` | What should I pay for gas right now? | `blocks` (1 to 1024, default 20), `percentiles` (default `[10, 50, 90]`) |
| `estimate_gas` | How much gas would this transaction use? | `from`, `to`, `data`, `value`, `block` |
| **Contracts and events** | | |
| `call_function` | Call a view function by signature, with decoded results | `to`, `signature`, `args`, `block`, `from`, `gas` |
| `call` | Raw `eth_call` with ABI-encoded calldata | `to`, `data`, `gas` (required), `from`, `value`, `block` |
| `get_logs` | Raw event logs in a block range, paged | `fromBlock`, `toBlock`, `address` (up to 32), `topics` (up to 4 positions), `cursor`, `limit` (default 100), `maxBytes` (default 128 KB) |
| `decode_logs` | Turn logs into named events with formatted amounts | `txHash` (or `hash`) or `logs` (up to 256), `abi` (extra event signatures, up to 32) |
| `get_storage_at` | One raw storage slot (for example an EIP-1967 proxy slot) | `address`, `slot`, `block` |
| `get_proof` | EIP-1186 Merkle proof of an account and slots | `address`, `storageKeys` (up to 64), `block` |

### Node health

*"Is my node healthy?"*, *"Why is my node behind?"*, *"Can my node answer questions about last year?"*

`node_status` reports the chain, client version, head block and its age, sync state (modes, lag, snap/fast sync and
backfill progress), peers, the state backend (Flat, HalfPath or Hash, archive or pruned) with the oldest block that
has state, the oldest block with bodies and receipts, whether trace and debug tools work, the log index range, and
plain-English `warnings` such as 0 peers, a stale head or an unfinished sync. Agents should call it first, and again
whenever another tool returns `unavailable`.

### Transactions and blocks

*"Why did 0x… fail?"*, *"What did this transaction do?"*, *"Would this swap go through if I raised the gas limit?"*,
*"What happened in the latest block?"*

- `explain_transaction` gives a plain-English `summary` plus status, fees (base fee burnt, or sent to the fee
  collector on Gnosis, and the priority tip; for blob transactions `total` includes the blob fee, which is also shown
  on its own next to `executionFee`), the called method, decoded token movements with net flows per address,
  internal native transfers, and for failures the decoded revert reason, failing call frame and out-of-gas detection.
  When the sender itself moved no tokens (a bot or smart account calling a contract), the summary names what the
  called contract swapped instead. Parts the node can't serve are listed in `notes` instead of failing the whole call,
  and so are parts that don't fit the time budget: token metadata stops at about 30% of `Mcp.ToolTimeout`, and the
  call trace is abandoned after at most half of it. A transaction still in the mempool is reported as `pending`, with
  a maximum fee that includes the blob fee cap.
- `trace_transaction` replays the transaction and returns its call tree, capped at 300 frames by default (`maxFrames`,
  up to `Mcp.MaxTraceCalls`), 24 levels and a byte budget below `Mcp.MaxResultSize`. `truncated` and per-frame
  `omittedCalls` say what was cut. The node's call tracer builds the whole tree, with every frame's call data, in
  memory before it is cut, so tracing a transaction with a huge number of calls costs node memory for the duration of
  the call; `maxDepth=0` records only the top-level call and is cheap.
- `simulate_transaction` dry-runs one call or a sequence (such as approve then swap) with optional state overrides,
  via `eth_simulateV1`. Nothing is signed or stored, and gas isn't charged.
- `block_summary` covers transaction counts by type, gas, base fee, burnt fees, blobs, withdrawals (ETH on Ethereum,
  GNO on Gnosis) and top recipients and tokens.

### Addresses, tokens and names

*"What tokens does vitalik.eth hold?"*, *"What is 0x…?"*, *"Is this really USDC?"*

The agent typically chains `resolve_ens` → `token_balances`, or uses `lookup_address` for a one-call profile. The
node has no token index, so `token_balances` checks a built-in token list by default, which exists only on mainnet
(WETH, USDC, USDT, DAI, WBTC, stETH, wstETH, GNO) and Gnosis (WXDAI, GNO, USDC, WETH, sDAI). On other chains,
omitting `tokens` returns only the native balance. Token names and symbols come from the contracts themselves and are
untrusted: summaries mark the symbol of any token outside that list as `SYMBOL (unverified token 0x…)`. Every on-chain
string that reaches a client (token names and symbols, ABI-decoded `string` values from `call_function` and
`decode_logs`, revert strings, ENS names) and every node error message is sanitised per Unicode code point: control,
format (bidi overrides, zero-width characters, the invisible Unicode TAG characters used for "ASCII smuggling"),
private-use and unassigned characters and variation selectors are removed, emoji and other visible text are kept, and
long values are cut without splitting a character (decoded strings at 1,024 characters, with a truncation marker).
The server instructions tell the agent never to follow instructions found in such data. `token_info` and
`lookup_address` detect EIP-1967 (implementation and beacon), ZeppelinOS (used by USDC), EIP-1167 minimal and EIP-897
(`implementation()` getter) proxies. ENS works on mainnet, Sepolia and Holesky only, and fails with `unavailable`
elsewhere.

### Gas and fees

*"What should I pay for gas right now?"*, *"How much would it cost to send 1 ETH?"*

`fee_estimate` combines `eth_feeHistory` with the node's gas price oracle: current and next base fee, slow, standard
and fast `maxPriorityFeePerGas` and `maxFeePerGas` in wei and gwei, block fullness and its trend, the cost of a
21,000-gas transfer in the native currency, and blob fees when active. `estimate_gas` sizes a specific transaction.

### Contracts and events

*"What's the total supply of this token?"*, *"Show the last Transfer events of this contract"*, *"Which
implementation is this proxy using?"*

- `call_function` takes a human-readable signature such as `balanceOf(address) returns (uint256)` and JSON arguments,
  and returns decoded outputs. Prefer it over the raw `call`.
- `get_logs` pages through ranges of any size. Each page scans at most `Mcp.MaxLogBlockRange` blocks (1,000 by
  default) and returns at most 100 logs and about 128 KB of logs by default; `limit` (up to `Mcp.MaxLogs`) and
  `maxBytes` (up to about three quarters of `Mcp.MaxResultSize`) raise that. When `truncated` is `true`, call again with **exactly the same** `fromBlock`, `toBlock`,
  `address` and `topics` plus `cursor` set to `nextCursor`, until `truncated` is `false`. The response's
  `fromBlock`/`toBlock` say which blocks that page covered. A range ending at `latest` keeps the end resolved by the
  first page. Cursors are opaque, can't be edited, and expire when the node restarts. A cursor also becomes invalid
  (`invalid_input`) if the chain reorganises past its position; restart the query then.
- If `fromBlock` is below the oldest block the node keeps receipts for, the scan starts at that block and the page
  reports `clampedFromBlock` (the requested start) and a `note`. A range entirely below it fails with `unavailable`.
- With the log index enabled (`LogIndex.Enabled`), a page whose filter has an address or topic and starts inside the
  indexed range can span up to `Mcp.MaxIndexedLogBlockRange` blocks (1,000,000 by default), and the response has
  `indexed: true`. Without an address or topic, pages always use the smaller range. Filtering is much faster either
  way, so encourage the agent to filter.
- `decode_logs` decodes common events (ERC-20/721/1155 transfers and approvals, WETH/WXDAI deposits and withdrawals
  (only from the chain's wrapped native token contract; other `Deposit`/`Withdrawal` events get no standard),
  Uniswap V2/V3 swaps and more) and any event signatures you pass in `abi`.

## Resources and prompts

### Resources

| URI | Type | Content |
|---|---|---|
| `nethermind://chain` | JSON | Chain ID, network name, testnet flag, native currency (xDAI on Gnosis and Chiado) with decimals, staking token (GNO on Gnosis, ETH elsewhere), current fork, genesis hash, head number and well-known contracts. |
| `nethermind://contracts` | JSON | System contracts from the chain spec (deposit contract, EIP-4788, EIP-2935, EIP-7002, EIP-7251), the ENS registry where it exists, and a curated list of major tokens with symbol and decimals (mainnet and Gnosis only). |
| `nethermind://guide` | Markdown | A guide for agents: which tool answers which question, block selectors, units, error codes and what to do about each, this node's limits, and Gnosis notes on Gnosis chains. |

Resources are built from in-memory chain facts and never touch the database or the EVM. The server's `instructions`
(sent at initialization) name the network and currency and tell the agent to start with `node_status` and the guide.

### Prompts

Ready-made investigation recipes that tell the agent which tools to call and how to answer. Arguments are validated,
and invalid ones fail the `prompts/get` request.

| Prompt | Arguments | What it does |
|---|---|---|
| `investigate_transaction` | `hash` | Explains a transaction end to end: transfers, token movements, contracts called, fee and outcome. |
| `why_did_my_transaction_fail` | `hash` | Finds the revert reason or out-of-gas cause and the failing call, and optionally checks a fix with `simulate_transaction`. |
| `summarize_address` | `address` | Account type, native balance, well-known token holdings, contract details and ENS name. |
| `check_node_health` | none | Sync state, head age, peers and available history, with next steps. |
| `gas_advice` | none | Slow, normal and fast fee suggestions in gwei, the expected cost, and the fee trend. |
| `token_report` | `owner` | A table of native and well-known token balances. |

## Output conventions

A successful call returns `structuredContent` of `{"result": ...}`, which matches the tool's declared output schema,
plus a text content block with the same JSON (so the payload travels twice; `MaxResultSize` limits the JSON once).
A failed call sets `isError: true` and returns only a text content block with
`{"error": {"code": "...", "message": "...", "data": ...}}`, without `structuredContent`, because the output schema
describes the success shape. `data` is optional. For `call`, `estimate_gas` and `call_function` reverts, `data` holds
the raw revert data as hex (cut to 4 KB, then `dataTruncated: true` and `dataSize` in bytes), and `reason`
(`{kind, message, selector}`) holds the decoded reason.

| Code | Meaning | What to do |
|---|---|---|
| `invalid_input` | An argument is missing, malformed or out of range. | Fix it as the message says (formats and ranges are spelled out). |
| `not_found` | No such block, transaction or receipt on this node. | Check the hash and the network (`chain_info`). A transaction may not be mined yet, or may be older than the node's history (see below). |
| `execution_reverted` | The EVM call reverted. | Read the decoded reason. Retrying unchanged won't help. |
| `unavailable` | The node doesn't have the data (pruned state or history, still syncing, ENS not on this chain, method not available). | Use a block inside the range the message names, or use a node that keeps that data (archive or full history). |
| `resource_exhausted` | A limit was hit: result size, concurrency, input caps, or the node is busy. | Narrow the query or page with the cursor or offset. For concurrency, retry shortly. |
| `timeout` | The tool ran longer than `Mcp.ToolTimeout`. | Narrow the query. Don't retry a large request in a loop. |
| `internal_error` | An unexpected failure. Details go to the node log only. | Retry once, then check the node log. |

Protocol-level problems (unknown tool, malformed JSON-RPC, invalid prompt arguments) come back as standard JSON-RPC
errors. HTTP-level rejections are covered under [Troubleshooting](#troubleshooting).

**Raw plus human-readable fields.** Tools that mirror JSON-RPC (`get_block`, `get_transaction`, `get_logs`, ...)
return exactly what the `eth_*` method returns: hex quantities in wei and gas units, `0x` data. The higher-level
tools keep those raw values and add readable fields next to them:

- amounts: `"value": "0xde0b6b3a7640000", "valueFormatted": "1", "symbol": "ETH"`, and token amounts use the token's
  decimals;
- timestamps: `timestampIso` in UTC;
- gas prices: gwei decimal strings (`baseFeeGwei`, `maxFeePerGasGwei`);
- addresses in readable fields: EIP-55 checksummed;
- long lists: bounded, with `truncated`, `nextCursor`, `nextOffset`, `omitted` or `skipped` saying what was left out.

**Native currency.** On Gnosis and Chiado the native currency is **xDAI**: balances, gas and fees are in xDAI, never
ETH. Validators stake **GNO**, an ERC-20 token, and beacon-chain withdrawals on Gnosis are paid in GNO by the deposit
contract, not in xDAI. `block_summary` reports withdrawal totals accordingly. Testnet results are flagged as having no
value.

## Node types and data availability

What the server can answer depends on what the node stores. The tools never fake an answer: when data is missing,
they fail with `unavailable` and name the range the node does keep, for example `State for block 19000000 is not
available on this node: it keeps state for blocks 20999937..21000000 (pruned, HalfPath: about the last 64 blocks).
Use a recent block (for example "latest"), or query an archive node.`

| Question | Default snap/fast-synced node | Archive node |
|---|---|---|
| Balances, code, storage, `call`, `call_function`, `token_*`, `lookup_address`, `resolve_ens`, `estimate_gas`, `simulate_transaction`, `get_proof` at `latest` | Yes | Yes |
| Same at an old block | Only within the recent state window | Yes |
| `trace_transaction`, and the internal transfers in `explain_transaction`, for old transactions (needs the parent block's state) | Recent blocks only. `explain_transaction` still answers and lists the gap in `notes`. | Yes |
| Transactions, receipts, logs and block bodies | From the node's oldest stored body and receipt onwards | Same (depends on the history settings, not on state) |

- **State.** A pruned node (HalfPath, or Flat without a long history window) keeps state for recent blocks only.
  Archive configs such as `-c mainnet_archive` or `-c gnosis_archive` keep all of it.
- **History.** Fast sync doesn't download bodies and receipts below the network's ancient barriers (the bundled
  mainnet, Sepolia and Gnosis configs set `Sync.AncientBodiesBarrier` and `Sync.AncientReceiptsBarrier`), and not at
  all with `Sync.DownloadBodiesInFastSync=false` or `Sync.DownloadReceiptsInFastSync=false` (then the node keeps
  only the bodies and receipts of the blocks it processed after syncing). History expiry (`History.Pruning`) drops
  old bodies and receipts too. The server finds the oldest stored body and receipt block by probing the stores
  (a binary search of about 25 key lookups each, cached for five minutes), not from the sync pivot, which moves on
  every pivot update; `node_status`, the `get_logs` clamp and the error messages name those blocks.
  With `Receipt.StoreReceipts=false`, receipt, log and status queries fail. Error
  messages name the setting that caused the gap.
- **Old transactions by hash.** When a node never stored a transaction's block body, looking it up by hash returns
  `not_found`, not `unavailable`, because the node has no record of that hash. When the node's history is limited,
  the message names the first block it keeps transactions from.
  `node_status` tells you whether that's the likely cause.
- **Ranges.** `node_status` reports `state.oldestBlock`, `history.oldestBodyBlock` and `history.oldestReceiptBlock`
  as decimal numbers (`null` when unknown). Treat them as guidance: on some backends state availability isn't a
  clean range, so the server checks each request against the node's actual state instead of rejecting based on the
  range alone.
- **Trace and debug.** `trace_transaction` and the call trace in `explain_transaction` use the node's `debug` module
  in-process (`debug_traceTransaction` with the native call tracer). This works whether or not `debug` or `trace`
  is listed in `JsonRpc.EnabledModules`, which only gates the public JSON-RPC endpoint. Set `Mcp.EnableTracing=false`
  to turn it off. `node_status.features` reports whether both are available.

## Configuration reference

All keys live in the `Mcp` section (`--Mcp.<Key>` on the command line, `NETHERMIND_MCPCONFIG_<KEY>` as environment
variables). Arrays are comma-separated on the command line. All numeric limits must be positive.

| Key | Default | Description |
|---|---|---|
| `Enabled` | `false` | Starts the MCP server. |
| `Host` | `127.0.0.1` | IP address to bind. Loopback (`127.0.0.1`, `::1`) serves local clients. Any other address, including `0.0.0.0` and `::`, is [remote mode](#e-remote-mode). Host names are rejected. |
| `Port` | `8555` | TCP port. The endpoint is `http(s)://<Host>:<Port>/mcp`. Must not collide with any JSON-RPC, WebSocket, Engine API, additional RPC URL or metrics port. |
| `AuthTokenFile` | `null` | File holding the bearer token (trimmed, at least 32 characters). Optional on loopback, required in remote mode. |
| `AllowedOrigins` | `[]` | Browser origins (`http(s)://host[:port]`, such as `http://localhost:6274`) allowed to call the endpoint. Requests without `Origin` are allowed. |
| `AllowedHosts` | `[]` | Extra accepted `Host` header values (`host` or `host:port`). Added to the loopback names on loopback. In remote mode, the only names accepted (at least one is required). |
| `TlsCertificatePath` | `null` | PEM certificate (plus optional chain). Required in remote mode. Enables HTTPS on loopback. |
| `TlsCertificateKeyPath` | `null` | Unencrypted PEM private key (PKCS#8, PKCS#1 or SEC1) of the certificate. Required whenever `TlsCertificatePath` is set. |
| `MaxRequestBodySize` | `262144` | Maximum HTTP request body, in bytes. Larger requests get HTTP 413. |
| `MaxConcurrentToolCalls` | `4` | Tool calls running at once. A further call waits up to 2 seconds for a free slot (clients often issue several calls in parallel), then fails with `resource_exhausted`. `node_status` has its own reserved slot and still answers when all of them are busy. |
| `ToolTimeout` | `10000` | Wall-clock limit per tool call, in ms. Must not exceed `JsonRpc.Timeout` (default 20000). |
| `MaxResultSize` | `4194304` | Maximum serialized tool result, in bytes. Larger results fail with `resource_exhausted`. |
| `MaxLogBlockRange` | `1000` | Blocks one `get_logs` page scans when the log index doesn't cover it. Larger ranges are paged. |
| `MaxIndexedLogBlockRange` | `1000000` | Blocks one `get_logs` page may scan when the filter has an address or topic and the log index covers the range. |
| `MaxLogs` | `10000` | Maximum logs per `get_logs` page: the upper bound of `limit` (whose default is 100). |
| `MaxCallGas` | `50000000` | Gas cap for `call`, `call_function`, `estimate_gas` and `simulate_transaction`. Must not exceed `JsonRpc.GasCap` (default 100000000); the effective cap is the smaller of the two. |
| `MaxCallDataSize` | `131072` | Maximum call data, in bytes, for the call-style tools. |
| `MaxTraceCalls` | `2000` | Maximum call frames returned by `trace_transaction`: the upper bound of `maxFrames` (whose default is 300). Larger trees are truncated and flagged. |
| `EnableTracing` | `true` | Allows `trace_transaction` and the call-trace parts of `explain_transaction`, which use the debug module in-process regardless of `JsonRpc.EnabledModules`. When `false`, `trace_transaction` fails with `unavailable` and `explain_transaction` skips the trace with a note. |

Fixed limits that aren't configurable: 32 `get_logs` addresses, 4 topic positions and 32 hashes per position; 50
tokens in `token_balances`; 256 logs and 32 signatures in `decode_logs`; 64 proof keys; 8 simulated calls and 8
overridden accounts (64 slots each); 24 trace levels; 1,000 receipts per `get_block_receipts` page; 1,000 full
transactions per `get_block` page; 1,024 blocks and 10 percentiles in `fee_estimate`.

**Tuning:**

- LLM clients pay for every byte. The paged tools already default to small pages; the 4 MiB `MaxResultSize` only
  bounds what an agent can ask for explicitly. Lowering it to around 1 MiB also caps those explicit requests.
- On a small machine, or when several people share one node, keep `MaxConcurrentToolCalls` low. Tool calls rent
  the same JSON-RPC module pools as your public RPC traffic.
- If traces or simulations time out on busy blocks, raise `ToolTimeout`, and `JsonRpc.Timeout` with it if needed.
  Multi-part tools (`lookup_address`, `token_balances`) stop at about 70% of the timeout and report what they skipped,
  and `explain_transaction` leaves out token metadata or the call trace with a note rather than timing out.
- Enable the log index if agents often search events over long ranges.

## Security model

- **Loopback by default.** The listener binds `127.0.0.1` and has its own port and its own web host, separate from
  JSON-RPC and the Engine API. It reads no ASP.NET settings, environment variables or command line of its own, so
  nothing outside the Nethermind config can add listeners.
- **Host check** (DNS-rebinding defence). On loopback, `Host` must be `127.0.0.1:<port>`, `localhost:<port>`,
  `[::1]:<port>` or an `AllowedHosts` entry. In remote mode, only `AllowedHosts` entries and the bound IP address.
  Anything else gets HTTP 400.
- **Origin check.** Requests with an `Origin` header not listed in `AllowedOrigins` get HTTP 403. The server never
  sends CORS headers, so browsers can't read responses cross-origin.
- **Bearer token.** Optional on loopback, mandatory in remote mode. Tokens are compared as SHA-256 digests in constant
  time. Failures get HTTP 401 with `WWW-Authenticate: Bearer`, and in remote mode repeated failures are throttled
  (HTTP 429).
- **Remote mode** needs HTTPS (TLS 1.2 or 1.3), a token and `AllowedHosts`. Plain HTTP is never served on a
  non-loopback address.
- **Separate secrets.** The Engine API JWT is never used or accepted. Keep the MCP token separate.
- **No secrets in logs.** Tokens and request bodies are never logged. The embedded web server logs at warning level
  and above only, because lower levels can include request content. Clients never see stack traces.
- **Read-only tools.** All tools read state or run calls in memory. Nothing is signed, broadcast or persisted.
- **Bounded work.** Per-tool timeout, a concurrency cap (extra calls wait up to 2 seconds, then fail), result and request size caps, input
  caps (see [Configuration](#configuration-reference)), and HTTP limits: HTTP/1.1 only, 64 concurrent connections, a
  32 KB header budget with at most 64 headers, and a 10-second header timeout. When a call times out, the client
  gets `timeout` right away, but the work keeps its concurrency slot until the underlying RPC call returns (bounded
  by `JsonRpc.Timeout`), so runaway work can't pile up.
- **Clean shutdown.** When the node stops, the MCP server first rejects new tool calls (`unavailable`: the node is
  shutting down), cancels running ones and waits for them within the 5-second stop budget, so no tool call still reads
  the databases while the node closes them. A call stuck inside an uninterruptible RPC method is logged instead.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| HTTP 400 | `Host` header not accepted. Usually a forwarded port that differs from `Mcp.Port`, a Docker-published port in remote mode without a matching `AllowedHosts` entry, or connecting by a DNS name. | Use the same port on both ends of the tunnel, or add the exact `host:port` clients send to `Mcp.AllowedHosts`. |
| HTTP 401 | Missing or wrong `Authorization: Bearer <token>`. | Send the trimmed content of the token file. Check that your client actually sends headers (some need a restart after a config change). |
| HTTP 403 | The request has an `Origin` header that isn't allowed (browser-based clients, the Inspector). | Add the exact origin to `Mcp.AllowedOrigins`, such as `http://localhost:6274`. |
| HTTP 404 | Wrong path. | The only endpoint is `/mcp`. |
| HTTP 413 | Request body above `Mcp.MaxRequestBodySize`. | Send smaller arguments (for example fewer raw logs to `decode_logs`), or raise the limit. |
| HTTP 429 | Remote mode: 10 failed authentications from your address within 60 s. | Fix the token, then wait for the `Retry-After` period (60 s). |
| Node exits at startup: `Mcp.Port 8555 collides with ...` | Port shared with JSON-RPC, WebSocket, Engine API, an additional RPC URL or metrics. | Choose another `Mcp.Port`. |
| Node exits at startup: `Failed to start the MCP server on 127.0.0.1:8555: ...` | The port is already in use by another process (or another node). | Free the port or choose another. An enabled MCP server that can't start aborts node startup by design. |
| Node exits at startup naming `Mcp.Host`, `Mcp.AuthTokenFile`, `Mcp.Tls*` or `Mcp.AllowedHosts` | Invalid value, or remote mode without its requirements. | Follow the message. See the [remote mode requirements](#e-remote-mode). |
| Client can't connect over HTTPS | The client doesn't trust the certificate, or the certificate lacks the name you connect with. | See [trusting a self-signed certificate](#trusting-a-self-signed-certificate). Check `subjectAltName`. |
| `unavailable` for old blocks | Pruned state, or bodies and receipts not stored for that range. | Ask about recent blocks, check the ranges in `node_status`, or use an archive node. |
| `unavailable` while syncing | The node doesn't have head state yet. | Wait for sync. `node_status` shows progress and warnings. |
| `not_found` for an old transaction | The node never stored that block's body. | See [Node types](#node-types-and-data-availability). |
| `timeout` | Heavy trace, simulation, large block or wide log range. | Narrow the request, page with the cursor, or raise `Mcp.ToolTimeout` (at most `JsonRpc.Timeout`). |
| `resource_exhausted: Too many concurrent tool calls` | The agent fires tools in parallel beyond `Mcp.MaxConcurrentToolCalls`, and no slot freed up within 2 seconds. | Retry, or raise the limit. |
| `resource_exhausted: The node is busy serving other RPC requests` | The JSON-RPC module pool is saturated by other traffic. | Retry later. |

## Building the plugin

The plugin ships with the Nethermind runner. To build it on its own:

```bash
# From the repository root
cd src/Nethermind
dotnet build Nethermind.Mcp.Plugin/Nethermind.Mcp.Plugin.csproj -c Release
```
