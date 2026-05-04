# MCP Review Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Land four fixes for the code-review findings on `feat/mcp-server`: a pure-projector test-infrastructure rule carveout, well-known bind-address resolution in `McpWebHost`, an empty-`ApiKey` guard tightening, and removal of the dead `EnabledTools` config.

**Architecture:** Four independently revertable commits. F1 is a docs/rule edit only. F2 adds an `internal static` host-resolution helper plus tests. F3 tightens the auth guard with a parameterized test. F4 removes a never-consumed property from the config interface, default class, test, and design doc. No new abstractions, no new dependencies.

**Tech Stack:** .NET 10, C# 14, NUnit (with `[TestCase]` parameterization), NSubstitute, ASP.NET Core Kestrel, `ModelContextProtocol` 1.1. No new packages.

**Reference Design:** `docs/plans/2026-05-04-mcp-review-fixes-design.md`.

**Reference Rules to Reread Before Each Task:**
- `.agents/rules/coding-style.md`
- `.agents/rules/robustness.md`
- `.agents/rules/test-infrastructure.md`

---

## Conventions Used in This Plan

- One commit per task. Conventional-commit style.
- TDD where there's logic to test. F1 (rule update) and F4 (deletion) have no logic to TDD.
- File paths are absolute from repo root.

---

## Task 1: Add pure-projector exception to test-infrastructure rule

**Goal:** Document the carveout for adapter/projector classes whose collaborators have no behavior to verify.

**Files:**
- Modify: `.agents/rules/test-infrastructure.md` (append a new section after the existing "DI anti-pattern" section)

**Step 1: Read the current file**

Read `/home/maceo/neth/nethermind/.agents/rules/test-infrastructure.md` end-to-end. Identify the insertion point — immediately after the "DI anti-pattern — never manually new up infrastructure" section (after the line `The rule: **if production modules already wire a component, use them — don't construct it yourself**.`) and before "Test guidelines".

**Step 2: Append the new section**

Insert verbatim:

```markdown

## Pure projector exception

When the unit under test is a *pure projector* — its only behavior is reading properties from injected services and assembling a DTO, with no branching on service state, no I/O, no caching, and no orchestration — `Substitute.For<>()` on the collaborators is acceptable. The integration test that wires the projector into the rest of the system is the safety net.

A class qualifies when it satisfies all of:

- Methods read collaborator properties or call collaborator getter-only methods.
- Output is a DTO whose fields are direct or arithmetic transformations of the inputs.
- No persistence, no event publication, no logging-as-side-effect.
- No branching on collaborator return values beyond null/empty handling.

When in doubt, prefer the rule and use `TestBlockchain`. Pure projectors are uncommon — most services do enough work that real collaborators catch real bugs.

Example: `Nethermind.Mcp.Adapter.NethermindNodeAdapter` projects `IBlockTree.Head.Number`, `ISyncPeerPool.PeerCount`, etc. into MCP DTOs; substituting collaborators keeps adapter tests focused on the projection contract while `IntegrationTests` cover real-services wire-up.
```

**Step 3: Verify markdown renders**

```bash
grep -n "Pure projector exception" /home/maceo/neth/nethermind/.agents/rules/test-infrastructure.md
```
Expected: one match line referencing the new section header.

**Step 4: Commit**

```bash
git add .agents/rules/test-infrastructure.md
git commit -m "docs: clarify pure-projector test exception"
```

---

## Task 2: Resolve well-known bind addresses in `McpWebHost`

**Goal:** `Mcp.HttpHost = "localhost"` (and `0.0.0.0`, `::`, `::1`) boot successfully. Numeric IPs unchanged. Arbitrary hostnames still fail (out of scope).

**Files:**
- Modify: `src/Nethermind/Nethermind.Mcp/Hosting/McpWebHost.cs` — extract `ResolveBindAddress` helper, swap call site.
- Modify: `src/Nethermind/Nethermind.Mcp.Test/Hosting/McpWebHostTests.cs` — add unit test for the helper plus one end-to-end case.

### Step 1: Write the failing tests

In `src/Nethermind/Nethermind.Mcp.Test/Hosting/McpWebHostTests.cs`, add:

```csharp
[TestCase("localhost", "127.0.0.1")]
[TestCase("LOCALHOST", "127.0.0.1")]
[TestCase("127.0.0.1", "127.0.0.1")]
[TestCase("0.0.0.0", "0.0.0.0")]
[TestCase("::", "::")]
[TestCase("[::]", "::")]
[TestCase("::1", "::1")]
[TestCase("[::1]", "::1")]
[TestCase("192.168.1.5", "192.168.1.5")]
public void ResolveBindAddress_maps_well_known_names(string input, string expected)
{
    System.Net.IPAddress address = McpWebHost.ResolveBindAddress(input);
    Assert.That(address, Is.EqualTo(System.Net.IPAddress.Parse(expected)));
}

[Test]
public void ResolveBindAddress_throws_FormatException_for_arbitrary_hostname()
{
    Assert.Throws<System.FormatException>(() => McpWebHost.ResolveBindAddress("mynode.local"));
}
```

Also add the end-to-end "localhost binds successfully" test, mirroring the existing `Start_binds_to_configured_port_and_serves_mcp_endpoint` setup but with `HttpHost = "localhost"`:

```csharp
[Test]
public async Task Start_with_localhost_binds_to_loopback()
{
    McpConfig config = new() { Enabled = true, HttpEnabled = true, HttpHost = "localhost", HttpPort = 0 };
    await using McpWebHost host = NewHost(config);

    bool started = await host.StartAsync(default);

    Assert.That(started, Is.True);
    Assert.That(host.BoundUri, Is.Not.Null);
    Assert.That(host.BoundUri!.Host, Is.EqualTo("127.0.0.1"));
}
```

(`NewHost(...)` is the existing helper in `McpWebHostTests.cs`. If the helper is named differently, match it.)

### Step 2: Run tests; verify failure

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release -- --filter "FullyQualifiedName~ResolveBindAddress|FullyQualifiedName~Start_with_localhost"
```
Expected: compile error — `McpWebHost.ResolveBindAddress` does not exist. (Or NotFound for the start test if it runs.)

### Step 3: Implement the helper

In `src/Nethermind/Nethermind.Mcp/Hosting/McpWebHost.cs`, add a new method next to `ReadBoundUri`:

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

### Step 4: Update the call site in `BuildApp`

In the same file, locate the line:

```csharp
IPAddress address = IPAddress.Parse(_config.HttpHost);
```

Replace with:

```csharp
IPAddress address = ResolveBindAddress(_config.HttpHost);
```

### Step 5: Run tests; verify pass

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release -- --filter "FullyQualifiedName~ResolveBindAddress|FullyQualifiedName~Start_with_localhost"
```
Expected: PASS for all parameterized cases plus the end-to-end test.

### Step 6: Run the full Mcp.Test pass

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release
```
Expected: all prior tests still green; net new = ~10 cases.

### Step 7: Commit

```bash
git add src/Nethermind/Nethermind.Mcp/Hosting/McpWebHost.cs src/Nethermind/Nethermind.Mcp.Test/Hosting/McpWebHostTests.cs
git commit -m "fix(mcp): resolve localhost/0.0.0.0/:: bind addresses"
```

---

## Task 3: Treat empty `ApiKey` as auth disabled

**Goal:** `Mcp.ApiKey = ""` no longer authenticates. Behaves identically to unset.

**Files:**
- Modify: `src/Nethermind/Nethermind.Mcp/Hosting/ApiKeyAuthMiddleware.cs:29`
- Modify: `src/Nethermind/Nethermind.Mcp.Test/Hosting/ApiKeyAuthMiddlewareTests.cs` — parameterize the existing pass-through test.

### Step 1: Find the existing pass-through test

Read `src/Nethermind/Nethermind.Mcp.Test/Hosting/ApiKeyAuthMiddlewareTests.cs`. Locate the test currently named (approximately) `When_apiKey_is_null_request_passes_through`. Note its current `[Test]` attribute and the fact it constructs an `IMcpConfig` (or `McpConfig`) with `ApiKey = null` and asserts `next` ran and status is not 401.

### Step 2: Modify the failing test to also cover empty string

Rename and parameterize:

```csharp
[TestCase(null)]
[TestCase("")]
public async Task When_apiKey_is_unset_request_passes_through(string? apiKey)
{
    // body identical to the prior null-only test, but constructed with `ApiKey = apiKey`.
    // Keep all existing assertions: next was invoked AND status code is not 401.
}
```

(Adjust the closure / config wiring to use the parameter. The rest of the assertions are unchanged.)

### Step 3: Run; expect the empty-string case to fail

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release -- --filter "FullyQualifiedName~When_apiKey_is_unset_request_passes_through"
```
Expected: the `null` case passes, the `""` case fails with status 401 (because the current guard is `expected is null`).

### Step 4: Tighten the guard

In `src/Nethermind/Nethermind.Mcp/Hosting/ApiKeyAuthMiddleware.cs`, replace:

```csharp
string? expected = config.ApiKey;
if (expected is null)
{
    await next(context);
    return;
}
```

With:

```csharp
string? expected = config.ApiKey;
if (string.IsNullOrEmpty(expected))
{
    await next(context);
    return;
}
```

### Step 5: Run tests; verify both cases pass

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release -- --filter "FullyQualifiedName~When_apiKey"
```
Expected: every `When_apiKey_*` test passes. Both `null` and `""` go through the pass-through path. The set-key tests still enforce 401 on missing/wrong bearer.

### Step 6: Run the full Mcp.Test pass

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release
```
Expected: all green.

### Step 7: Commit

```bash
git add src/Nethermind/Nethermind.Mcp/Hosting/ApiKeyAuthMiddleware.cs src/Nethermind/Nethermind.Mcp.Test/Hosting/ApiKeyAuthMiddlewareTests.cs
git commit -m "fix(mcp): treat empty ApiKey as auth disabled"
```

---

## Task 4: Drop unused `EnabledTools` config

**Goal:** Remove the property from `IMcpConfig`, `McpConfig`, the test, and the design doc. v2 can reintroduce when filtering is actually wired.

**Files:**
- Modify: `src/Nethermind/Nethermind.Mcp/IMcpConfig.cs` — delete the `EnabledTools` property and its `[ConfigItem]` attribute.
- Modify: `src/Nethermind/Nethermind.Mcp/McpConfig.cs` — delete the `EnabledTools` line.
- Modify: `src/Nethermind/Nethermind.Mcp.Test/McpConfigTests.cs` — drop the `EnabledTools` assertion.
- Modify: `docs/plans/2026-05-03-nethermind-mcp-server-design.md` — drop the `EnabledTools` row from §4 configuration table and any other reference.

### Step 1: Confirm no consumers

```bash
grep -rn "EnabledTools" /home/maceo/neth/nethermind/src/Nethermind/ /home/maceo/neth/nethermind/docs/
```
Expected: matches only in `IMcpConfig.cs`, `McpConfig.cs`, `McpConfigTests.cs`, and `2026-05-03-nethermind-mcp-server-design.md`. If any other file matches, stop and ask.

### Step 2: Delete from `IMcpConfig.cs`

Remove the trailing block (after `MaxConcurrent`):

```csharp
    [ConfigItem(Description = "Whitelist of tool categories to expose. `*` means all enabled categories.", DefaultValue = "[*]")]
    string[] EnabledTools { get; set; }
```

### Step 3: Delete from `McpConfig.cs`

Remove the line:

```csharp
    public string[] EnabledTools { get; set; } = ["*"];
```

### Step 4: Update `McpConfigTests.cs`

Remove the assertion:

```csharp
Assert.That(config.EnabledTools, Is.EqualTo(new[] { "*" }));
```

### Step 5: Update the design doc

In `docs/plans/2026-05-03-nethermind-mcp-server-design.md`, the §4 configuration block is a C# code block listing the seven properties. Remove the line:

```csharp
    string[] EnabledTools { get; set; }   // default ["*"]
```

Also drop any prose mention of `EnabledTools` if present (search the doc).

### Step 6: Run the test that previously asserted on `EnabledTools`

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release -- --filter "FullyQualifiedName~Defaults_match_design"
```
Expected: PASS.

### Step 7: Run the full Mcp.Test pass

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release
```
Expected: all green.

### Step 8: Sanity-build the runner (the property removal could break consumers)

```bash
dotnet build /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj -c release
```
Expected: success. There are no consumers, so the build should be clean. If it fails, investigate.

### Step 9: Commit

```bash
git add src/Nethermind/Nethermind.Mcp/IMcpConfig.cs src/Nethermind/Nethermind.Mcp/McpConfig.cs src/Nethermind/Nethermind.Mcp.Test/McpConfigTests.cs docs/plans/2026-05-03-nethermind-mcp-server-design.md
git commit -m "refactor(mcp): drop unused EnabledTools config"
```

---

## Task 5: Final verification

**Goal:** Confirm the full surface still passes after all four fixes.

### Step 1: Run the full Mcp test project

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release
```
Expected: all tests pass. Net count: prior 59 + ~10 from `ResolveBindAddress` parameterized cases + 1 from the localhost end-to-end test + 1 from the new auth-passthrough TestCase, − 1 for the `EnabledTools` assertion that was inside an existing test. Roughly 70 total — the exact number depends on parameterization expansion.

### Step 2: Run the runner-level plugin tests

```bash
dotnet test --project /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Runner.Test/Nethermind.Runner.Test.csproj -c release -- --filter "FullyQualifiedName~BuiltInPlugins|FullyQualifiedName~PluginDisposal"
```
Expected: all pass.

### Step 3: Format

```bash
dotnet format whitespace /home/maceo/neth/nethermind/src/Nethermind/ --folder
```
Expected: no diff. If a diff is produced, stage and commit it as `style(mcp): apply dotnet format`.

### Step 4: Sanity-build

```bash
dotnet build /home/maceo/neth/nethermind/src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj -c release
```
Expected: success.

### Step 5: Walk through success criteria from the design doc §6

For each of the five criteria in `docs/plans/2026-05-04-mcp-review-fixes-design.md` §6, confirm met. Report any criterion not met.

### Step 6: No commit needed if Steps 1–4 are clean

---

## Notes for the Executor

- **Each task's commit is independent** — if one fails review, it can be reverted without disturbing the others.
- **TDD discipline** — F2 and F3 follow write-failing-test → run-fails → implement → run-passes. F1 is docs-only. F4 is a deletion; the existing test catches accidental regression.
- **Parameterized tests** — F3's TestCase replacement removes one literal test and adds two `TestCase` rows; net pass-count change is 0 (`null` was already covered) plus the new `""` row. Do not add a separate `When_apiKey_is_empty…` method — that would duplicate.
- **Design doc edit in F4** — `2026-05-03-nethermind-mcp-server-design.md` is checked in; the edit is permitted because it's documentation tracking shipped surface, not retroactive rewriting of approved decisions.
