# MCP Review Fixes — Design

**Date:** 2026-05-04
**Status:** Approved
**Source:** Code review of `feat/mcp-server` branch (4 findings — see review report).

---

## 1. Decisions

| # | Finding | Fix |
|---|---|---|
| F1 | Test-infra rule conflict (adapter tests use `Substitute.For` instead of `TestBlockchain`) | Update `.agents/rules/test-infrastructure.md` with a "pure projector" carveout. No test code change. |
| F2 | `HttpHost = "localhost"` fails `IPAddress.Parse` and is swallowed by fail-soft | Resolve well-known names (`localhost`, `0.0.0.0`, `::`, `::1`) to the right `IPAddress` inside `McpWebHost`. No DNS lookup. |
| F3 | Empty `ApiKey` authenticates `Authorization: Bearer ` (empty token) | Change `ApiKeyAuthMiddleware` guard from `expected is null` to `string.IsNullOrEmpty(expected)`. |
| F4 | `IMcpConfig.EnabledTools` declared but never consulted | Remove from `IMcpConfig`, `McpConfig`, the v1 design's §4 configuration table, and `McpConfigTests.Defaults_match_design`. |

## 2. Detailed changes

### F1 — `.agents/rules/test-infrastructure.md`

Append a new section after "DI anti-pattern":

> ## Pure projector exception
>
> When the unit under test is a *pure projector* — its only behavior is reading properties from injected services and assembling a DTO, with no branching on service state, no I/O, no caching, and no orchestration — `Substitute.For<>()` on the collaborators is acceptable. The integration test that wires the projector into the rest of the system is the safety net.
>
> A class qualifies when it satisfies all of: methods read collaborator properties or getter-only methods; output is a direct or arithmetic transformation of inputs; no persistence, no event publication, no logging-as-side-effect; no branching on collaborator return values beyond null/empty handling.
>
> When in doubt, prefer the rule and use `TestBlockchain`. Pure projectors are uncommon — most services do enough work that real collaborators catch real bugs.
>
> Example: `Nethermind.Mcp.Adapter.NethermindNodeAdapter` projects `IBlockTree.Head.Number`, `ISyncPeerPool.PeerCount`, etc. into MCP DTOs; substituting collaborators keeps adapter tests focused on the projection contract while `IntegrationTests` cover real-services wire-up.

### F2 — `src/Nethermind/Nethermind.Mcp/Hosting/McpWebHost.cs`

Add a small helper next to `ReadBoundUri`:

```csharp
internal static IPAddress ResolveBindAddress(string host)
{
    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
    if (host == "0.0.0.0") return IPAddress.Any;
    if (host == "::" || host == "[::]") return IPAddress.IPv6Any;
    if (host == "::1" || host == "[::1]") return IPAddress.IPv6Loopback;
    return IPAddress.Parse(host);
}
```

Replace the existing `IPAddress.Parse(_config.HttpHost)` call site (line ~177) with `ResolveBindAddress(_config.HttpHost)`. `internal static` so the test project (already has `InternalsVisibleTo`) can unit-test directly.

Test additions in `Nethermind.Mcp.Test/Hosting/McpWebHostTests.cs`:
- One parameterized `[TestCase]` covering `localhost`, `127.0.0.1`, `0.0.0.0`, `::`, `[::]`, `::1`, `[::1]` mapping to the right `IPAddress` instance.
- Optionally one end-to-end test that starts a host with `HttpHost = "localhost"` and asserts `BoundUri` is the loopback address (re-uses the existing `Start_binds…` infrastructure).

Numeric IP literal path unchanged. Arbitrary hostnames (`mynode.local`) still throw `FormatException` as before — operator gets the same error they get today; we've fixed the common case.

### F3 — `src/Nethermind/Nethermind.Mcp/Hosting/ApiKeyAuthMiddleware.cs`

Change line 29:

```csharp
string? expected = config.ApiKey;
if (string.IsNullOrEmpty(expected))
{
    await next(context);
    return;
}
```

Test changes in `Nethermind.Mcp.Test/Hosting/ApiKeyAuthMiddlewareTests.cs`: parameterize the existing pass-through test as `[TestCase(null)] [TestCase("")] When_apiKey_is_unset_request_passes_through(string? key)`. Removes the implicit duplication.

### F4 — `IMcpConfig`, `McpConfig`, design

- Delete the `EnabledTools` property and its `[ConfigItem]` attribute from `src/Nethermind/Nethermind.Mcp/IMcpConfig.cs`.
- Delete the `EnabledTools` line from `src/Nethermind/Nethermind.Mcp/McpConfig.cs`.
- Drop the `EnabledTools` assertion from `Nethermind.Mcp.Test/McpConfigTests.cs::Defaults_match_design`.
- Drop the `EnabledTools` row from the v1 design doc's §4 configuration table (`docs/plans/2026-05-03-nethermind-mcp-server-design.md`).

If/when v2 wires actual filtering, reintroduce the property at that time with the consumer in the same PR.

## 3. Commit strategy

Four commits, each independently revertable:

1. `docs: clarify pure-projector test exception` — `.agents/rules/test-infrastructure.md` only.
2. `fix(mcp): resolve localhost/0.0.0.0/:: bind addresses` — `McpWebHost.cs` + tests.
3. `fix(mcp): treat empty ApiKey as auth disabled` — `ApiKeyAuthMiddleware.cs` + parameterized test.
4. `refactor(mcp): drop unused EnabledTools config` — `IMcpConfig.cs` + `McpConfig.cs` + `McpConfigTests.cs` + design doc.

Bundling would be readable too (small total diff), but separate commits map cleanly to the four review findings.

## 4. Out of scope

- Per-client rate limiting, OAuth, stdio — already deferred to v2 per the original design.
- Switching the integration test to `TestBlockchain` — larger separate change; mocked `INethermindApi` is current state and the pure-projector exception covers it.
- Hostname (DNS) resolution beyond the `localhost` short-circuit — adds blocking I/O at startup; not justified for v1.

## 5. Verification

After all four commits:
- `dotnet test --project src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release` — expect 59 → roughly 60 (one new `ResolveBindAddress` test plus the parameterized auth pass-through, minus one assertion in `Defaults_match_design`).
- `dotnet format whitespace src/Nethermind/ --folder` — clean.
- `dotnet build src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj -c release` — clean.

## 6. Success criteria

1. `.agents/rules/test-infrastructure.md` carries the pure-projector carveout with the example reference.
2. `Mcp.HttpHost = "localhost"` boots successfully; existing numeric-IP behavior unchanged.
3. `Mcp.ApiKey = ""` behaves identically to unset (auth disabled); `ApiKey = "secret"` still enforces; empty bearer with set key still 401.
4. `EnabledTools` no longer appears in `IMcpConfig`, `McpConfig`, the design's configuration table, or any test assertion.
5. All Mcp.Test tests green; runner build clean.
