# Nethermind MCP Server v1 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship an opt-in `Nethermind.Mcp` plugin that exposes a Model Context Protocol HTTP/SSE server on a dedicated Kestrel host, surfacing 3 diagnostic tools, 1 chain query tool, and 2 resources, with optional Bearer-key auth.

**Architecture:** New project `src/Nethermind/Nethermind.Mcp/` registered as an embedded plugin. The plugin owns its own Kestrel host, mounts the `ModelContextProtocol.AspNetCore` SSE handler at `/mcp`, and routes tool/resource calls through a thin read-only `INethermindNodeAdapter` that wraps `IBlockTree`, `ISyncServer`, `ISyncPeerPool`, and process/GC stats. Default off (`Mcp.Enabled=false`). Sibling test project `Nethermind.Mcp.Test` covers adapter, tools, resources, middleware, and end-to-end SSE flow.

**Tech Stack:** .NET 10, C# 14, NUnit, NSubstitute, Autofac, ASP.NET Core Kestrel, `ModelContextProtocol` 1.1, `ModelContextProtocol.AspNetCore` 1.1, `System.Text.Json` source generation.

**Reference Design:** `docs/plans/2026-05-03-nethermind-mcp-server-design.md`.

**Reference Rules to Reread Before Each Task:**
- `.agents/rules/coding-style.md`
- `.agents/rules/di-patterns.md`
- `.agents/rules/test-infrastructure.md`
- `.agents/rules/robustness.md`
- `.agents/rules/package-management.md`

---

## Conventions Used in This Plan

- **Test runner.** Each `dotnet test` example uses `--filter` to scope to the just-written test. Final task runs the full project test pass.
- **Commit cadence.** One commit per task. Conventional-commit style (`feat:`, `test:`, `chore:`).
- **TDD.** Every task with non-trivial logic follows write-failing-test → run-it-fails → implement-minimal → run-passes → commit. Tasks with no logic to test (csproj edits, slnx edits, DTO type-only declarations) are clearly marked as setup-only.
- **File paths are absolute from repo root.**

---

## Task 1: Scaffold `Nethermind.Mcp` and `Nethermind.Mcp.Test` projects

**Goal:** Empty buildable plugin + test projects, CPM entries for MCP SDK, slnx wired up. No behavior yet.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/Nethermind.Mcp.csproj`
- Create: `src/Nethermind/Nethermind.Mcp/PlaceholderType.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj`
- Create: `src/Nethermind/Nethermind.Mcp.Test/PlaceholderTest.cs`
- Modify: `Directory.Packages.props` (add `ModelContextProtocol`, `ModelContextProtocol.AspNetCore` at version `1.1.0`)
- Modify: `src/Nethermind/Nethermind.slnx` (add the two project entries — see existing `Nethermind.HealthChecks` lines 11–12 and 65/116 for placement convention)

**Step 1: Add the package versions to CPM**

Edit `Directory.Packages.props`. Locate the `<ItemGroup>` block of `<PackageVersion>` items and insert (in alphabetical position):

```xml
<PackageVersion Include="ModelContextProtocol" Version="1.1.0" />
<PackageVersion Include="ModelContextProtocol.AspNetCore" Version="1.1.0" />
```

**Step 2: Write `Nethermind.Mcp.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Nethermind.Api\Nethermind.Api.csproj" />
    <ProjectReference Include="..\Nethermind.JsonRpc\Nethermind.JsonRpc.csproj" />
  </ItemGroup>

</Project>
```

**Step 3: Write a placeholder type so the assembly has at least one symbol**

`src/Nethermind/Nethermind.Mcp/PlaceholderType.cs`:

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp;

internal static class PlaceholderType
{
    public const string Name = "Nethermind.Mcp";
}
```

**Step 4: Write `Nethermind.Mcp.Test.csproj`** (mirrors `Nethermind.HealthChecks.Test/Nethermind.HealthChecks.Test.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Import Project="../tests.props" />

  <ItemGroup>
    <PackageReference Include="NSubstitute" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Nethermind.Core.Test\Nethermind.Core.Test.csproj" />
    <ProjectReference Include="..\Nethermind.Mcp\Nethermind.Mcp.csproj" />
  </ItemGroup>

</Project>
```

**Step 5: Add a placeholder test that asserts the project loaded**

`src/Nethermind/Nethermind.Mcp.Test/PlaceholderTest.cs`:

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Mcp.Test;

public class PlaceholderTest
{
    [Test]
    public void Project_loads()
    {
        Assert.That(typeof(Nethermind.Mcp.PlaceholderType).Namespace, Is.EqualTo("Nethermind.Mcp"));
    }
}
```

**Step 6: Add both projects to `src/Nethermind/Nethermind.slnx`**

Insert in alphabetical position (model after the `Nethermind.HealthChecks` and `Nethermind.HealthChecks.Test` entries):

```xml
<Project Path="Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj" />
<Project Path="Nethermind.Mcp/Nethermind.Mcp.csproj" />
```

(Both top-level project list and any `<Folder>` group the existing plugins live in.)

**Step 7: Build and run the placeholder test**

```bash
dotnet build src/Nethermind/Nethermind.Mcp/Nethermind.Mcp.csproj -c release
dotnet test src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release --filter FullyQualifiedName~Project_loads
```
Expected: build succeeds; one test passes.

**Step 8: Commit**

```bash
git add Directory.Packages.props src/Nethermind/Nethermind.slnx src/Nethermind/Nethermind.Mcp src/Nethermind/Nethermind.Mcp.Test
git commit -m "chore(mcp): scaffold Nethermind.Mcp plugin and test projects"
```

---

## Task 2: `IMcpConfig` interface and `McpConfig` defaults

**Goal:** Plugin config available via `IConfigProvider` with the design's defaults. TDD-validated round-trip.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/IMcpConfig.cs`
- Create: `src/Nethermind/Nethermind.Mcp/McpConfig.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/McpConfigTests.cs`

**Step 1: Write the failing test** in `McpConfigTests.cs`:

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Mcp.Test;

public class McpConfigTests
{
    [Test]
    public void Defaults_match_design()
    {
        IMcpConfig config = new McpConfig();

        Assert.That(config.Enabled, Is.False);
        Assert.That(config.HttpEnabled, Is.True);
        Assert.That(config.HttpHost, Is.EqualTo("127.0.0.1"));
        Assert.That(config.HttpPort, Is.EqualTo(8550));
        Assert.That(config.ApiKey, Is.Null);
        Assert.That(config.MaxConcurrent, Is.EqualTo(4));
        Assert.That(config.EnabledTools, Is.EqualTo(new[] { "*" }));
    }
}
```

**Step 2: Run test to verify it fails to compile**

```bash
dotnet test src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release --filter FullyQualifiedName~Defaults_match_design
```
Expected: compile error — `IMcpConfig` not found.

**Step 3: Write `IMcpConfig.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Mcp;

public interface IMcpConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the MCP server plugin.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Whether to enable the HTTP/SSE MCP transport.", DefaultValue = "true")]
    bool HttpEnabled { get; set; }

    [ConfigItem(Description = "Bind address for the MCP HTTP/SSE listener.", DefaultValue = "127.0.0.1")]
    string HttpHost { get; set; }

    [ConfigItem(Description = "Port for the MCP HTTP/SSE listener.", DefaultValue = "8550")]
    int HttpPort { get; set; }

    [ConfigItem(Description = "If set, requires `Authorization: Bearer <key>` on every MCP request.", DefaultValue = "null")]
    string? ApiKey { get; set; }

    [ConfigItem(Description = "Maximum concurrent MCP tool invocations.", DefaultValue = "4")]
    int MaxConcurrent { get; set; }

    [ConfigItem(Description = "Whitelist of tool categories to expose. `*` means all enabled categories.", DefaultValue = "[*]")]
    string[] EnabledTools { get; set; }
}
```

**Step 4: Write `McpConfig.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp;

public class McpConfig : IMcpConfig
{
    public bool Enabled { get; set; } = false;
    public bool HttpEnabled { get; set; } = true;
    public string HttpHost { get; set; } = "127.0.0.1";
    public int HttpPort { get; set; } = 8550;
    public string? ApiKey { get; set; } = null;
    public int MaxConcurrent { get; set; } = 4;
    public string[] EnabledTools { get; set; } = ["*"];
}
```

**Step 5: Run test to verify pass**

```bash
dotnet test src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release --filter FullyQualifiedName~Defaults_match_design
```
Expected: PASS.

**Step 6: Delete the placeholder type and test**

```bash
rm src/Nethermind/Nethermind.Mcp/PlaceholderType.cs
rm src/Nethermind/Nethermind.Mcp.Test/PlaceholderTest.cs
```

Rebuild to confirm nothing else referenced them:
```bash
dotnet build src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release
```

**Step 7: Commit**

```bash
git add src/Nethermind/Nethermind.Mcp src/Nethermind/Nethermind.Mcp.Test
git commit -m "feat(mcp): add IMcpConfig with v1 defaults"
```

---

## Task 3: DTO types for v1 surface

**Goal:** All DTOs exist as immutable records. No logic, no tests — just type contracts that adapter and tools will consume in subsequent tasks.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/Dto/SyncStatusDto.cs`
- Create: `src/Nethermind/Nethermind.Mcp/Dto/NodeHealthDto.cs`
- Create: `src/Nethermind/Nethermind.Mcp/Dto/NodeVersionDto.cs`
- Create: `src/Nethermind/Nethermind.Mcp/Dto/BlockSummaryDto.cs`
- Create: `src/Nethermind/Nethermind.Mcp/Dto/NodeConfigDto.cs`

**Step 1: Write `SyncStatusDto.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Dto;

public sealed record SyncStatusDto(
    long CurrentBlock,
    long HighestKnownBlock,
    string SyncMode,
    long BlocksBehind,
    int PeerCount);
```

**Step 2: Write `NodeHealthDto.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Dto;

public sealed record HealthCheckDto(string Name, string Status, string? Message, object? Value);

public sealed record NodeHealthDto(
    string OverallStatus,
    HealthCheckDto[] Checks,
    long UptimeSeconds,
    long ProcessMemoryMb,
    int GcGen0Collections,
    int GcGen1Collections,
    int GcGen2Collections,
    long? DiskFreeGb,
    long? DiskUsedGb);
```

**Step 3: Write `NodeVersionDto.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Dto;

public sealed record NodeVersionDto(
    string ClientVersion,
    string DotNetRuntime,
    string OperatingSystem,
    string[] EnabledRpcModules);
```

**Step 4: Write `BlockSummaryDto.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Dto;

public sealed record BlockTransactionSummary(
    string Hash,
    string From,
    string? To,
    string Value,
    long? GasUsed);

public sealed record BlockSummaryDto(
    long Number,
    string Hash,
    string ParentHash,
    long Timestamp,
    long GasUsed,
    long GasLimit,
    string? BaseFeePerGas,
    int TransactionCount,
    string FeeRecipient,
    string StateRoot,
    long Size,
    BlockTransactionSummary[] Transactions);
```

**Step 5: Write `NodeConfigDto.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Mcp.Dto;

public sealed record NodeConfigDto(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Sections);
```

**Step 6: Build to confirm everything compiles**

```bash
dotnet build src/Nethermind/Nethermind.Mcp/Nethermind.Mcp.csproj -c release
```
Expected: success.

**Step 7: Commit**

```bash
git add src/Nethermind/Nethermind.Mcp/Dto
git commit -m "feat(mcp): add v1 DTO records"
```

---

## Task 4: `INethermindNodeAdapter` + `GetNodeVersion`

**Goal:** Smallest adapter slice end-to-end so the pattern is established. Subsequent adapter tasks just add methods.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/Adapter/INethermindNodeAdapter.cs`
- Create: `src/Nethermind/Nethermind.Mcp/Adapter/NethermindNodeAdapter.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/Adapter/NethermindNodeAdapterTests.cs`

**Step 1: Write the failing test**

`Nethermind.Mcp.Test/Adapter/NethermindNodeAdapterTests.cs`:

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Mcp.Adapter;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Test.Adapter;

public class NethermindNodeAdapterTests
{
    [Test]
    public void GetNodeVersion_returns_client_version_and_runtime()
    {
        INethermindApi api = Substitute.For<INethermindApi>();

        INethermindNodeAdapter adapter = new NethermindNodeAdapter(api);

        NodeVersionDto version = adapter.GetNodeVersion();

        Assert.That(version.ClientVersion, Is.EqualTo(ProductInfo.ClientId));
        Assert.That(version.DotNetRuntime, Does.StartWith(".NET"));
        Assert.That(version.OperatingSystem, Is.Not.Empty);
    }
}
```

**Step 2: Run to verify failure**

```bash
dotnet test src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release --filter FullyQualifiedName~GetNodeVersion_returns_client_version_and_runtime
```
Expected: compile error — `INethermindNodeAdapter` missing.

**Step 3: Write `INethermindNodeAdapter.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Adapter;

public interface INethermindNodeAdapter
{
    NodeVersionDto GetNodeVersion();
}
```

**Step 4: Write `NethermindNodeAdapter.cs`**

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Api;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Adapter;

public sealed class NethermindNodeAdapter(INethermindApi api) : INethermindNodeAdapter
{
    public NodeVersionDto GetNodeVersion() => new(
        ClientVersion: ProductInfo.ClientId,
        DotNetRuntime: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        EnabledRpcModules: System.Array.Empty<string>());
}
```

**Step 5: Run test to verify pass**

```bash
dotnet test src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release --filter FullyQualifiedName~GetNodeVersion_returns_client_version_and_runtime
```
Expected: PASS.

**Step 6: Commit**

```bash
git add src/Nethermind/Nethermind.Mcp/Adapter src/Nethermind/Nethermind.Mcp.Test/Adapter
git commit -m "feat(mcp): add NethermindNodeAdapter.GetNodeVersion"
```

---

## Task 5: `NethermindNodeAdapter.GetSyncStatus`

**Goal:** Exercise `IBlockTree`, `ISyncServer`, `ISyncPeerPool` against a `TestBlockchain`-style setup. Substitute mocks for v1 — `TestBlockchain` integration is reserved for the integration test in Task 16.

**Files:**
- Modify: `src/Nethermind/Nethermind.Mcp/Adapter/INethermindNodeAdapter.cs`
- Modify: `src/Nethermind/Nethermind.Mcp/Adapter/NethermindNodeAdapter.cs`
- Modify: `src/Nethermind/Nethermind.Mcp.Test/Adapter/NethermindNodeAdapterTests.cs`

**Step 1: Add the failing test**

```csharp
[Test]
public void GetSyncStatus_returns_current_and_best_known_block_with_peer_count()
{
    INethermindApi api = Substitute.For<INethermindApi>();
    var head = Build.A.Block.WithNumber(100).TestObject.Header;
    var bestSuggested = Build.A.Block.WithNumber(150).TestObject.Header;
    api.BlockTree!.Head.Returns(Build.A.Block.WithHeader(head).TestObject);
    api.BlockTree!.BestSuggestedHeader.Returns(bestSuggested);
    api.SyncServer!.Returns(Substitute.For<ISyncServer>());
    api.SyncPeerPool!.PeerCount.Returns(7);

    INethermindNodeAdapter adapter = new NethermindNodeAdapter(api);
    var status = adapter.GetSyncStatus();

    Assert.That(status.CurrentBlock, Is.EqualTo(100));
    Assert.That(status.HighestKnownBlock, Is.EqualTo(150));
    Assert.That(status.BlocksBehind, Is.EqualTo(50));
    Assert.That(status.PeerCount, Is.EqualTo(7));
    Assert.That(status.SyncMode, Is.Not.Empty);
}
```

Add the necessary `using` lines (`Nethermind.Blockchain`, `Nethermind.Core.Test.Builders`, `Nethermind.Network`, `Nethermind.Synchronization`).

**Step 2: Run test to verify failure**

```bash
dotnet test src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release --filter FullyQualifiedName~GetSyncStatus_returns_current_and_best_known_block_with_peer_count
```
Expected: compile error — method missing on adapter.

**Step 3: Add `SyncStatusDto GetSyncStatus()` to interface**

**Step 4: Implement** in `NethermindNodeAdapter`:

```csharp
public SyncStatusDto GetSyncStatus()
{
    long current = api.BlockTree?.Head?.Number ?? 0;
    long highest = api.BlockTree?.BestSuggestedHeader?.Number ?? current;
    long behind = System.Math.Max(0, highest - current);
    int peerCount = api.SyncPeerPool?.PeerCount ?? 0;
    string mode = behind == 0 ? "Idle" : "Syncing";

    return new SyncStatusDto(current, highest, mode, behind, peerCount);
}
```

**Step 5: Run to verify pass**

```bash
dotnet test src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release --filter FullyQualifiedName~GetSyncStatus_returns_current_and_best_known_block_with_peer_count
```
Expected: PASS.

**Step 6: Commit**

```bash
git add src/Nethermind/Nethermind.Mcp src/Nethermind/Nethermind.Mcp.Test
git commit -m "feat(mcp): add NethermindNodeAdapter.GetSyncStatus"
```

---

## Task 6: `NethermindNodeAdapter.GetNodeHealth`

**Goal:** Composite check returning sync, peer, memory, GC stats. No disk free yet (deferred — `Nethermind.HealthChecks.DriveInfoExtensions` is internal; we accept `null` for disk fields when `IFileSystem` isn't injected).

**Files:**
- Modify: `src/Nethermind/Nethermind.Mcp/Adapter/INethermindNodeAdapter.cs`
- Modify: `src/Nethermind/Nethermind.Mcp/Adapter/NethermindNodeAdapter.cs`
- Modify: `src/Nethermind/Nethermind.Mcp.Test/Adapter/NethermindNodeAdapterTests.cs`

**Step 1: Add failing test** verifying structure:
- `OverallStatus == "Healthy"` when 0 peers, in-sync, normal memory.
- Check entries exist for `sync`, `peers`, `memory`.
- `ProcessMemoryMb >= 0`.
- `UptimeSeconds >= 0`.

**Step 2: Run, observe compile failure.**

**Step 3: Implement.** Aggregate `GetSyncStatus()` + peer count + `GC.GetGCMemoryInfo()` + `Process.GetCurrentProcess().WorkingSet64` + `Process.GetCurrentProcess().StartTime`. Use simple thresholds:
- sync `Healthy` if `BlocksBehind <= 1`, else `Degraded`.
- peers `Healthy` if `PeerCount >= 5`, `Degraded` if 1–4, `Unhealthy` if 0.
- memory `Healthy` always in v1 (no useful threshold without baseline).

`OverallStatus` = worst of all checks.

**Step 4: Run, expect pass.**

**Step 5: Commit** as `feat(mcp): add NethermindNodeAdapter.GetNodeHealth`.

---

## Task 7: `NethermindNodeAdapter.GetBlock`

**Goal:** Wrap `IBlockTree.FindBlock` with `BlockParameter` input. Return `null` for not-found.

**Files:**
- Modify: `src/Nethermind/Nethermind.Mcp/Adapter/INethermindNodeAdapter.cs`
- Modify: `src/Nethermind/Nethermind.Mcp/Adapter/NethermindNodeAdapter.cs`
- Modify: `src/Nethermind/Nethermind.Mcp.Test/Adapter/NethermindNodeAdapterTests.cs`

**Step 1: Add tests:**
1. `GetBlock_by_latest_returns_head_summary` — `BlockTree.Head` is `Build.A.Block.WithNumber(42).WithTransactions(2, MuirGlacier.Instance).TestObject`; expect non-null DTO with 2 transactions.
2. `GetBlock_by_unknown_number_returns_null`.

**Step 2: Add method to interface**

```csharp
BlockSummaryDto? GetBlock(BlockParameter blockParameter);
```

**Step 3: Implement.** Resolve via `api.BlockTree!.FindBlock(blockParameter, ...)`. Map to `BlockSummaryDto`. Iterate `block.Transactions` to build `BlockTransactionSummary[]`.

**Step 4: Run failing tests, fix, commit.**

```bash
git commit -m "feat(mcp): add NethermindNodeAdapter.GetBlock"
```

---

## Task 8: Config redaction helper

**Goal:** Pure function `IReadOnlyDictionary<...> Redact(IReadOnlyDictionary<...>)` that walks the config tree and replaces any value at a key matching the redaction regex with `"[REDACTED]"`. Used by `NodeConfigResource` later.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/Adapter/ConfigRedactor.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/Adapter/ConfigRedactorTests.cs`

**Step 1: Write failing tests:**
1. `Redact_replaces_value_when_key_matches_secret_regex` — input `{ "Auth": { "JwtSecretFile": "/secret.txt" } }` → `{ "Auth": { "JwtSecretFile": "[REDACTED]" } }`.
2. `Redact_leaves_unmatched_keys_alone` — input `{ "JsonRpc": { "Port": 8545 } }` → equal.
3. `Redact_is_case_insensitive` — `apiKey` → redacted.
4. `Redact_descends_into_nested_dictionaries`.

**Step 2: Run, observe failure.**

**Step 3: Implement.** Single regex `new Regex("(key|secret|password|jwt|signer)", RegexOptions.IgnoreCase | RegexOptions.Compiled)`, recursive walk over `IReadOnlyDictionary<string, object?>`.

**Step 4: Run, expect pass.**

**Step 5: Commit.** `feat(mcp): add config redaction helper`.

---

## Task 9: `ApiKeyAuthMiddleware`

**Goal:** ASP.NET Core middleware that returns `401` if `Mcp.ApiKey` is set and the request lacks a matching `Authorization: Bearer <key>`. Pass-through when key is unset.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/Hosting/ApiKeyAuthMiddleware.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/Hosting/ApiKeyAuthMiddlewareTests.cs`

**Step 1: Write failing tests** using `DefaultHttpContext` and a `RequestDelegate` spy:
1. `When_apiKey_is_null_request_passes_through`.
2. `When_apiKey_is_set_and_header_matches_request_passes`.
3. `When_apiKey_is_set_and_header_missing_returns_401`.
4. `When_apiKey_is_set_and_bearer_value_wrong_returns_401`.
5. `Header_match_is_case_sensitive_for_token_but_not_for_scheme`.

**Step 2: Run failing tests.**

**Step 3: Implement** middleware as a class with `InvokeAsync(HttpContext ctx, IMcpConfig cfg, RequestDelegate next)`:
- If `cfg.ApiKey is null`, call `next(ctx)`.
- Else read `Authorization` header; expect `Bearer <key>`; compare token via `CryptographicOperations.FixedTimeEquals` on UTF-8 bytes; if mismatch, set status `401`, do not call `next`.

**Step 4: Run, expect pass.**

**Step 5: Commit.** `feat(mcp): add bearer-key auth middleware`.

---

## Task 10: Tool classes (`NodeStatusTools`, `NodeHealthTools`, `ChainQueryTools`)

**Goal:** Three SDK-attribute-decorated classes that delegate to the adapter and return DTOs. No business logic.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/Tools/NodeStatusTools.cs`
- Create: `src/Nethermind/Nethermind.Mcp/Tools/NodeHealthTools.cs`
- Create: `src/Nethermind/Nethermind.Mcp/Tools/ChainQueryTools.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/Tools/ToolsTests.cs`

**Step 1: Write failing tests** that:
1. Construct each tool class with a substituted `INethermindNodeAdapter`.
2. Invoke each tool method directly (we test the C# surface, not the MCP protocol).
3. Assert the adapter method was called and the DTO was returned unmodified.

For `ChainQueryTools.GetBlockAsync` test both the `latest` path and the `0xHASH` path. (See SDK 1.1 docs — most tools can be sync; `async` allowed but optional.)

**Step 2: Run, observe failure.**

**Step 3: Implement** each tool class. Sample shape for `NodeStatusTools`:

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ModelContextProtocol.Server;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Tools;

[McpServerToolType]
public sealed class NodeStatusTools(INethermindNodeAdapter adapter)
{
    [McpServerTool(Name = "get_sync_status"), System.ComponentModel.Description("Returns sync state, peer count, and blocks-behind.")]
    public SyncStatusDto GetSyncStatus() => adapter.GetSyncStatus();

    [McpServerTool(Name = "get_node_version"), System.ComponentModel.Description("Returns Nethermind, .NET runtime, and OS versions.")]
    public NodeVersionDto GetNodeVersion() => adapter.GetNodeVersion();
}
```

`ChainQueryTools.get_block` accepts `string blockId` (default `"latest"`) and converts to `BlockParameter` via `BlockParameter.Parse`.

**Step 4: Run, expect pass.**

**Step 5: Commit.** `feat(mcp): add v1 MCP tool classes`.

---

## Task 11: Resource classes (`NodeStatusResource`, `NodeConfigResource`)

**Goal:** Two URI-template resources backed by adapter + redaction.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/Resources/NodeStatusResource.cs`
- Create: `src/Nethermind/Nethermind.Mcp/Resources/NodeConfigResource.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/Resources/ResourcesTests.cs`

**Step 1: Write failing tests:**
1. `NodeStatusResource_returns_sync_and_health_combined` — combined DTO.
2. `NodeConfigResource_redacts_sensitive_keys` — given an `IConfigProvider` substitute returning a config with a `JwtSecretFile`, expect the resource output to redact it.

**Step 2: Run, observe failure.**

**Step 3: Implement**

```csharp
[McpServerResourceType]
public sealed class NodeStatusResource(INethermindNodeAdapter adapter)
{
    [McpServerResource(UriTemplate = "nethermind://node/status"), System.ComponentModel.Description("Composite node status — sync, peers, health.")]
    public object Read() => new
    {
        Sync = adapter.GetSyncStatus(),
        Health = adapter.GetNodeHealth(),
        Version = adapter.GetNodeVersion(),
    };
}
```

`NodeConfigResource` takes `IConfigProvider` + `ConfigRedactor`, builds a `Dictionary<string, IReadOnlyDictionary<string, object?>>` from registered configs, redacts, and returns the dictionary.

**Step 4: Run, expect pass.**

**Step 5: Commit.** `feat(mcp): add v1 MCP resources with config redaction`.

---

## Task 12: Plugin DI module (`McpServerPluginModule`)

**Goal:** Register adapter, tools, resources, config, and the (yet-to-be-written) `McpWebHost` in an Autofac `IModule`.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/McpServerPluginModule.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/McpServerPluginModuleTests.cs`

**Step 1: Write failing test** that builds an Autofac container with the module + a stub `INethermindApi`, and verifies that `INethermindNodeAdapter`, `NodeStatusTools`, `ChainQueryTools`, `NodeStatusResource`, `NodeConfigResource`, `ConfigRedactor`, and `IMcpConfig` all resolve.

**Step 2: Run, observe failure.**

**Step 3: Implement** mirroring `HealthCheckPluginModule` (`Nethermind.HealthChecks/HealthChecksPlugin.cs:83`):

```csharp
public sealed class McpServerPluginModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .AddSingleton<INethermindNodeAdapter, NethermindNodeAdapter>()
            .AddSingleton<ConfigRedactor>()
            .AddSingleton<NodeStatusTools>()
            .AddSingleton<NodeHealthTools>()
            .AddSingleton<ChainQueryTools>()
            .AddSingleton<NodeStatusResource>()
            .AddSingleton<NodeConfigResource>();
    }
}
```

Note: `IMcpConfig` is registered automatically through `ConfigRegistrationSource` (per `Nethermind.Api/Extensions/PluginLoader.cs:117`). We do not register it manually.

**Step 4: Run, expect pass.**

**Step 5: Commit.** `feat(mcp): add plugin DI module`.

---

## Task 13: `McpWebHost`

**Goal:** Owns its own Kestrel host on `Mcp.HttpHost:Mcp.HttpPort`, mounts `ModelContextProtocol.AspNetCore` SSE handler at `/mcp`, applies `ApiKeyAuthMiddleware` and a `MaxConcurrent` semaphore. Fail-soft: if startup throws (port collision), log Error and leave the rest of the node alone.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/Hosting/McpWebHost.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/Hosting/McpWebHostTests.cs`

**Step 1: Write failing tests:**
1. `Start_binds_to_configured_port_and_serves_mcp_endpoint` — start on port `0` (ephemeral), GET `/mcp` (SSE handshake), assert non-`404` response.
2. `Start_returns_false_when_port_in_use` — start one host on a port, then try to start a second on the same port, assert `false` and that no exception escapes.
3. `Stop_releases_the_port`.

**Step 2: Run, observe failure.**

**Step 3: Implement** with `WebApplication.CreateBuilder()`, `builder.Services.AddMcpServer().WithToolsFromAssembly().WithResourcesFromAssembly()`, `builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Parse(host), port))`, register `MapMcp("/mcp")`, install `ApiKeyAuthMiddleware`. Wrap `app.StartAsync()` in try/catch; on failure log via `ILogger` and return `false`.

**Step 4: Run, expect pass.**

**Step 5: Commit.** `feat(mcp): add MCP Kestrel WebHost with fail-soft startup`.

---

## Task 14: `McpServerPlugin` lifecycle

**Goal:** Plugin entry point: `Init` reads config and warns, `InitRpcModules` starts the WebHost when enabled, `DisposeAsync` stops it.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp/McpServerPlugin.cs`
- Create: `src/Nethermind/Nethermind.Mcp.Test/McpServerPluginTests.cs`

**Step 1: Write failing tests:**
1. `Plugin_is_disabled_by_default` — fresh container, `plugin.Enabled` is `false`.
2. `Init_logs_warning_when_host_is_non_loopback_without_apiKey` — set `HttpHost = "0.0.0.0"`, `ApiKey = null`. Assert ILogger received a `Warn` containing the substring `MCP exposed without authentication`.
3. `InitRpcModules_starts_WebHost_when_enabled` — substitute `McpWebHost`, verify `StartAsync` was awaited.
4. `InitRpcModules_does_not_throw_when_WebHost_start_fails` — substituted host returns `false`; assert plugin still completes `InitRpcModules`.
5. `DisposeAsync_stops_WebHost`.

**Step 2: Run, observe failure.**

**Step 3: Implement** mirroring `HealthChecksPlugin.cs:18`. Resolve `McpWebHost` lazily through the Autofac container (`api.Context.Resolve<McpWebHost>()`).

**Step 4: Run, expect pass.**

**Step 5: Commit.** `feat(mcp): add McpServerPlugin lifecycle`.

---

## Task 15: Register plugin in `NethermindPlugins.EmbeddedPlugins`

**Goal:** Plugin is discovered by the embedded plugin loader and shows up in `BuiltInPluginsTests`.

**Files:**
- Modify: `src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj` — add `<ProjectReference Include="..\Nethermind.Mcp\Nethermind.Mcp.csproj" />`.
- Modify: `src/Nethermind/Nethermind.Runner/NethermindPlugins.cs:11` — insert `typeof(Nethermind.Mcp.McpServerPlugin)` in alphabetical position.
- Modify: `src/Nethermind/Nethermind.Runner.Test/BuiltInPluginsTests.cs` — if it asserts a fixed count or names list, add the new plugin to the expectation.

**Step 1: Read `BuiltInPluginsTests.cs` to know what shape of update is needed.**

**Step 2: Update files in order: ProjectReference → plugin list → test expectation.**

**Step 3: Run plugin-loading tests**

```bash
dotnet test src/Nethermind/Nethermind.Runner.Test/Nethermind.Runner.Test.csproj -c release --filter FullyQualifiedName~BuiltInPlugins
```
Expected: PASS.

**Step 4: Commit.** `feat(mcp): register MCP plugin in embedded plugin list`.

---

## Task 16: End-to-end integration test

**Goal:** Spin up `McpServerPlugin` with `Mcp.Enabled=true` against a `TestBlockchain`-style setup, drive it via the SDK's in-process MCP client, exercise the full surface.

**Files:**
- Create: `src/Nethermind/Nethermind.Mcp.Test/IntegrationTests.cs`

**Step 1: Identify the in-process MCP client from `ModelContextProtocol` 1.1 SDK** (`McpClientFactory.CreateAsync` against an `HttpTransport` pointing at the started WebHost URL).

**Step 2: Write failing test** that:
1. Builds an Autofac container with `INethermindApi` substitute (filled with sane returns: `BlockTree.Head` numbered 100, `BestSuggestedHeader` numbered 100, `PeerCount` 5). `NodeVersionDto.ClientVersion` is sourced from `Nethermind.Core.ProductInfo.ClientId` directly, no mock needed.
2. Starts `McpWebHost` on an ephemeral port.
3. Connects an SDK client over HTTP/SSE.
4. Calls `client.ListToolsAsync()`, asserts the four tool names are present.
5. Calls `client.CallToolAsync("get_sync_status")`, asserts `CurrentBlock == 100`.
6. Calls `client.CallToolAsync("get_block", { "blockId": "latest" })`, asserts non-null block payload.
7. Calls `client.ReadResourceAsync("nethermind://node/status")`, asserts the JSON is parseable.
8. With `ApiKey` set on the host but no auth header on a raw `HttpClient`, asserts `401`.

**Step 3: Run, observe failures.** (You may need to iterate on tool registration plumbing in `McpWebHost` — that's expected; this test is the integration safety net.)

**Step 4: Make it pass.** Likely fixes touch `McpWebHost` (assembly scanning for `[McpServerToolType]`) and tool argument binding.

**Step 5: Commit.** `test(mcp): add end-to-end integration coverage`.

---

## Task 17: Final verification, format, summary commit

**Goal:** Confirm all success criteria from `docs/plans/2026-05-03-nethermind-mcp-server-design.md` §14 and leave the tree clean.

**Step 1: Run full Mcp test pass**

```bash
dotnet test src/Nethermind/Nethermind.Mcp.Test/Nethermind.Mcp.Test.csproj -c release
```
Expected: all tests pass.

**Step 2: Run runner-level plugin tests**

```bash
dotnet test src/Nethermind/Nethermind.Runner.Test/Nethermind.Runner.Test.csproj -c release --filter "FullyQualifiedName~BuiltInPlugins|FullyQualifiedName~PluginDisposal|FullyQualifiedName~EthereumRunner"
```
Expected: all pass.

**Step 3: Format**

```bash
dotnet format whitespace src/Nethermind/ --folder
```
Expected: no diff produced (or stage formatting changes if produced).

**Step 4: Sanity-build the runner**

```bash
dotnet build src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj -c release
```
Expected: success.

**Step 5: Manual smoke (optional but recommended)**

Start a node with `--Mcp.Enabled true --Init.ChainSpecPath ...sepolia.json` and connect with MCP Inspector to `http://127.0.0.1:8550/mcp`. Verify the four tools list and execute. Stop the node, confirm clean shutdown logs.

**Step 6: Commit any formatting changes**

```bash
git status
# only run if there are changes:
git add -A src/Nethermind/Nethermind.Mcp src/Nethermind/Nethermind.Mcp.Test
git commit -m "style(mcp): apply dotnet format"
```

**Step 7: Verify the success criteria checklist** from the design doc §14. Strike off anything not yet met; if any criterion is unmet, raise it instead of closing the task.

---

## Notes for the Executor

- **DI subtlety:** `IMcpConfig` is wired automatically by `ConfigRegistrationSource` once the project ships an `IConfig`-implementing interface. Do not register it explicitly in the module.
- **Plugin lifecycle:** `MustInitialize` is `false`. Per-plugin `Init` runs even when `Enabled` is false (just skip side effects). `InitRpcModules` should early-return when `!Enabled || !HttpEnabled`.
- **Logging:** Use `api.LogManager.GetClassLogger<TYPE>()`. Honor `IsDebug`/`IsInfo`/`IsWarn`/`IsError` guards before formatting (per `.agents/rules/coding-style.md`).
- **Cancellation:** All `*Async` methods take `CancellationToken` and forward it.
- **Fail-soft:** A failure in MCP startup (port collision, missing dep) must never throw out of plugin lifecycle methods. Log Error, disable the plugin in-place, continue.
- **Avoid premature abstraction:** v1 has 4 tools and 2 resources. Do not introduce a registry, a factory, or a base class. Three classes plus DTOs is enough.
