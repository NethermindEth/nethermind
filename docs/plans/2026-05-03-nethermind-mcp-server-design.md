# Nethermind MCP Server — v1 Design

**Date:** 2026-05-03
**Status:** Approved (brainstorming output)
**Source PRD:** `mcp-prd.md`
**Goal:** MVP that doubles as the seed PR for production. Bundled, default-off, reviewable in a single PR.

---

## 1. Decisions

| # | Decision | Choice |
|---|---|---|
| D1 | Tool data sourcing | Hybrid (wrap services directly + reserve wrap-RPC seam for Phase 2). v1 is effectively all wrap-services. |
| D2 | Transport | HTTP/SSE only. Stdio deferred to v2. |
| D3 | Distribution | Bundled in main Nethermind binary (`src/Nethermind/Nethermind.Mcp/`), default-off, separate Kestrel host on its own port. Follows `HealthChecks` / `Flashbots` precedent. |
| D4 | v1 tool surface | 3 tools + 1 chain query + 2 resources. PRD Phase 1 plus `get_block` to exercise the chain-query path. |
| D5 | Auth | Optional Bearer API key (`Mcp.ApiKey`). Localhost default. Loud warning if exposed without it. OAuth 2.1 deferred to v2. |

## 2. Project layout

```
src/Nethermind/Nethermind.Mcp/
  Nethermind.Mcp.csproj
  McpServerPlugin.cs              // INethermindPlugin
  McpServerPluginModule.cs        // Autofac IModule
  IMcpConfig.cs / McpConfig.cs
  Hosting/
    McpWebHost.cs                 // dedicated Kestrel host (mirrors Runner/JsonRpc/WebHost.cs)
    ApiKeyAuthMiddleware.cs
  Adapter/
    INethermindNodeAdapter.cs
    NethermindNodeAdapter.cs
  Tools/
    NodeStatusTools.cs            // get_sync_status, get_node_version
    NodeHealthTools.cs            // get_node_health
    ChainQueryTools.cs            // get_block
  Resources/
    NodeStatusResource.cs         // nethermind://node/status
    NodeConfigResource.cs         // nethermind://node/config
  Dto/
    SyncStatusDto.cs, NodeHealthDto.cs, NodeVersionDto.cs,
    BlockSummaryDto.cs, NodeConfigDto.cs

src/Nethermind/Nethermind.Mcp.Test/
  ...
```

Both projects added to `Nethermind.slnx`. Project follows the C# 14 / `net10.0` target inherited via `Directory.Build.props`. NuGet versions managed via Central Package Management.

## 3. Plugin lifecycle

`McpServerPlugin : INethermindPlugin`

- `Name = "Mcp"`, `Description = "Model Context Protocol server"`, `Author = "Nethermind"`.
- `Enabled => _config.Enabled` (default `false`). `MustInitialize = false`.
- `Init(api)`: bind `IMcpConfig`, log a `Warn` if `HttpHost` is non-loopback and `ApiKey` is null.
- `InitRpcModules()`: when `Enabled && HttpEnabled`, build and start `McpWebHost`. Bind failure logs `Error`, leaves the rest of the node untouched (PRD G5 — additive, never blocks consensus).
- `DisposeAsync`: stop the WebHost gracefully.
- `Module` returns `McpServerPluginModule` registering: `IMcpConfig`, `INethermindNodeAdapter` → `NethermindNodeAdapter`, the tool classes, the resource classes, `McpWebHost`.

Module shape mirrors `HealthCheckPluginModule` (`Nethermind.HealthChecks/HealthChecksPlugin.cs`).

## 4. Configuration

```csharp
public interface IMcpConfig : IConfig
{
    bool Enabled { get; set; }            // default false
    bool HttpEnabled { get; set; }        // default true
    string HttpHost { get; set; }         // default "127.0.0.1"
    int HttpPort { get; set; }            // default 8550
    string? ApiKey { get; set; }          // default null
    int MaxConcurrent { get; set; }       // default 4
    string[] EnabledTools { get; set; }   // default ["*"]
}
```

Maps automatically to CLI (`--Mcp.Enabled true`), env (`NETHERMIND_MCPCONFIG_ENABLED=true`), and JSON (`{ "Mcp": { ... } }`).

## 5. Transport

- Dedicated Kestrel host (own `WebApplicationBuilder`) bound to `Mcp.HttpHost:Mcp.HttpPort`.
- Mounts the `ModelContextProtocol.AspNetCore` 1.1 SSE handler at `/mcp`.
- `ApiKeyAuthMiddleware` short-circuits `401 Unauthorized` if `Mcp.ApiKey` is set and the request lacks a matching `Authorization: Bearer <key>`. Pass-through when unset.
- A single `SemaphoreSlim` (`Mcp.MaxConcurrent`) wraps tool invocation to cap concurrency.

## 6. Adapter and tools

`INethermindNodeAdapter` exposes:

| Method | Backing services |
|---|---|
| `SyncStatusDto GetSyncStatus()` | `IBlockTree`, `ISyncServer`, `IPeerPool` |
| `NodeHealthDto GetNodeHealth()` | `IBlockTree`, `IPeerPool`, `Process.GetCurrentProcess()`, `GC.GetGCMemoryInfo()`, `DriveInfoExtensions` |
| `NodeVersionDto GetNodeVersion()` | `Nethermind.Core.ProductInfo.ClientId`, `RuntimeInformation`, RPC module enable flags |
| `BlockSummaryDto? GetBlock(BlockParameter)` | `IBlockTree.FindBlock` |

All methods read-only. DTOs are plain records, serialized via `System.Text.Json`. The adapter is the single seam through which tools touch node internals.

Tool classes are MCP-SDK-attribute-decorated (`[McpServerToolType]`, `[McpServerTool]`). Each tool method takes typed input (or none) and returns a DTO. Resources implement template URIs. Tool/resource classes contain no business logic — they delegate to the adapter.

## 7. Config redaction

`NodeConfigResource` reads via `IConfigProvider.GetRawConfigs()`, walks the resulting object graph, and replaces any value whose key matches `/key|secret|password|jwt|signer/i` with `"[REDACTED]"`. One helper, unit-tested. Same pattern is applied by `get_node_config` tool when added.

## 8. Error handling

- Tools return DTOs with explicit nullable fields for "not found" cases (e.g., `block?: null` + `status: "not_found"`). They do not throw for missing data.
- Genuine exceptions bubble to the MCP SDK, which maps them to MCP error responses with the original request id. We rely on SDK handling rather than wrapping every method.
- Plugin startup errors (port collision, dependency missing) log `Error`, disable MCP, leave the rest of Nethermind running. The plugin must never crash the node.

## 9. Security (v1)

- Default bind 127.0.0.1.
- Optional `Mcp.ApiKey`. Warning logged when `HttpHost` is non-loopback and `ApiKey` is unset.
- Adapter is read-only by construction — only `Get*` methods exist. No tool can submit transactions or mutate config.
- Config redaction as in §7.
- `MaxConcurrent` cap (default 4).
- Out of v1: OAuth 2.1, per-client rate limiting, stdio transport.

## 10. Testing

- `Nethermind.Mcp.Test` project, NUnit, follows `.agents/rules/test-infrastructure.md`.
- **Adapter tests** against `TestBlockchain` — real services, no collaborator mocks. Verify DTO shapes for in-sync, mid-sync, and not-found cases.
- **Tool tests** with a mocked `INethermindNodeAdapter`. Validate output JSON shape, error mapping, and concurrency cap behavior.
- **Auth middleware tests** for unset key, set key + valid bearer, set key + invalid bearer, missing header.
- **Integration test**: spin up plugin against `TestBlockchain` with `McpWebHost` on a random ephemeral port. Drive it via the MCP SDK's in-process client. Cover: `tools/list`, `tools/call get_sync_status`, `resources/read nethermind://node/status`, `tools/call get_block { "blockId": "latest" }`, missing block, bad API key.
- No Sepolia/Holesky integration in v1 — too slow for CI.

## 11. Observability

- Logging via `INethermindApi.LogManager` (`McpServerPlugin`, `McpWebHost`, each tool class).
- Startup log line: bound URL, transport, auth mode (`with API key` / `no auth`).
- Tool invocations: `Debug` level (`tool=get_sync_status duration_ms=12`).
- Errors: `Error` level with exception detail.
- No Prometheus counters in v1. Deferred to Phase 2 alongside `diagnose_node`.

## 12. Out of scope (v1)

| Feature | Defer to |
|---|---|
| Stdio transport | v2 (separate bridge binary, not by hijacking node stdout) |
| OAuth 2.1 auth | v2 |
| Phase 2 tools (peers, chain queries beyond `get_block`, txpool, debug, prompts) | Phase 2 PRs |
| Phase 3 tools and `diagnose_node` correlation logic | Phase 3 |
| Health-module integration | Phase 2 (when `diagnose_node` lands) |
| Sharing Kestrel with JSON-RPC | Likely never; reassess only if operators ask |
| Per-client rate limiting | Phase 2 if needed |
| Prometheus metrics for MCP | Phase 2 |

## 13. Open implementation details

These are decided during coding, not blocking design approval:

- `System.Text.Json` source-gen attribute placement on DTOs (AOT-friendliness).
- Whether `McpWebHost` reuses Nethermind's `EthereumJsonSerializer` or relies on the SDK's default JSON. Default to SDK unless interop issues surface.
- Exact NLog rule additions, if any, for MCP-specific log levels.
- Test-project name follows Nethermind convention `Nethermind.Mcp.Test` (not `.Tests`).

## 14. Success criteria

The v1 PR is ready to merge when:

1. `Nethermind.Mcp` and `Nethermind.Mcp.Test` build cleanly and join the solution.
2. With `Mcp.Enabled=true`, MCP Inspector connects to `http://127.0.0.1:8550/mcp` and successfully calls `get_sync_status`, `get_node_health`, `get_node_version`, and `get_block { "blockId": "latest" }` against a running node.
3. Both resources return well-formed JSON, with `nethermind://node/config` redacting sensitive keys.
4. With `Mcp.ApiKey` set, a missing or wrong bearer returns `401`.
5. Disabled-by-default: a vanilla node startup with no `Mcp.*` config produces zero MCP-related side effects (no port bound, no warnings).
6. All adapter, tool, middleware, and integration tests pass under `dotnet test`.
7. `dotnet format whitespace src/Nethermind/ --folder` is clean.
