// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using Nethermind.Config;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;
using Nethermind.Mcp.Resources;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Test.Resources;

public class ResourcesTests
{
    [Test]
    public void NodeStatusResource_returns_sync_health_and_version()
    {
        INethermindNodeAdapter adapter = Substitute.For<INethermindNodeAdapter>();
        SyncStatusDto sync = new(CurrentBlock: 100, HighestKnownBlock: 150, SyncMode: "Syncing", BlocksBehind: 50, PeerCount: 7);
        NodeHealthDto health = new(
            OverallStatus: "Healthy",
            Checks: Array.Empty<HealthCheckDto>(),
            UptimeSeconds: 60,
            ProcessMemoryMb: 512,
            GcGen0Collections: 1,
            GcGen1Collections: 2,
            GcGen2Collections: 3,
            DiskFreeGb: null,
            DiskUsedGb: null);
        NodeVersionDto version = new(
            ClientVersion: "Nethermind/v1.0.0",
            DotNetRuntime: ".NET 10.0",
            OperatingSystem: "Linux",
            EnabledRpcModules: ["eth", "net"]);

        adapter.GetSyncStatus().Returns(sync);
        adapter.GetNodeHealth().Returns(health);
        adapter.GetNodeVersion().Returns(version);

        NodeStatusResource resource = new(adapter);
        NodeStatusResourceDto result = resource.Read();

        Assert.That(result.Sync, Is.SameAs(sync));
        Assert.That(result.Health, Is.SameAs(health));
        Assert.That(result.Version, Is.SameAs(version));
        adapter.Received(1).GetSyncStatus();
        adapter.Received(1).GetNodeHealth();
        adapter.Received(1).GetNodeVersion();
    }

    [Test]
    public void NodeStatusResource_has_expected_uri_template_and_class_attribute()
    {
        Type type = typeof(NodeStatusResource);
        Assert.That(type.GetCustomAttribute<McpServerResourceTypeAttribute>(), Is.Not.Null);

        MethodInfo method = type.GetMethod(nameof(NodeStatusResource.Read))!;
        McpServerResourceAttribute? attr = method.GetCustomAttribute<McpServerResourceAttribute>();
        Assert.That(attr, Is.Not.Null);
        Assert.That(attr!.UriTemplate, Is.EqualTo("nethermind://node/status"));
    }

    [Test]
    public void NodeConfigResource_redacts_sensitive_keys()
    {
        ConfigProvider provider = new();
        provider.Initialize();

        NodeConfigResource resource = new(provider, new ConfigRedactor());
        NodeConfigDto dto = resource.Read();

        Assert.That(dto.Sections, Is.Not.Empty);

        // The JsonRpc section should exist and JwtSecretFile must be redacted.
        Assert.That(dto.Sections.ContainsKey("JsonRpc"), Is.True, "Expected JsonRpc section");
        System.Collections.Generic.IReadOnlyDictionary<string, object?> jsonRpc = dto.Sections["JsonRpc"];
        Assert.That(jsonRpc.ContainsKey("JwtSecretFile"), Is.True, "Expected JwtSecretFile property");
        Assert.That(jsonRpc["JwtSecretFile"], Is.EqualTo("[REDACTED]"));

        // Non-sensitive keys are preserved (Port is an int, not redacted).
        Assert.That(jsonRpc.ContainsKey("Port"), Is.True);
        Assert.That(jsonRpc["Port"], Is.Not.EqualTo("[REDACTED]"));
    }

    [Test]
    public void NodeConfigResource_section_names_strip_Config_suffix()
    {
        ConfigProvider provider = new();
        provider.Initialize();

        NodeConfigResource resource = new(provider, new ConfigRedactor());
        NodeConfigDto dto = resource.Read();

        Assert.That(dto.Sections.Keys.Any(k => k.EndsWith("Config", StringComparison.Ordinal)), Is.False,
            "Section names should have the trailing 'Config' stripped.");
    }

    [Test]
    public void NodeConfigResource_has_expected_uri_template_and_class_attribute()
    {
        Type type = typeof(NodeConfigResource);
        Assert.That(type.GetCustomAttribute<McpServerResourceTypeAttribute>(), Is.Not.Null);

        MethodInfo method = type.GetMethod(nameof(NodeConfigResource.Read))!;
        McpServerResourceAttribute? attr = method.GetCustomAttribute<McpServerResourceAttribute>();
        Assert.That(attr, Is.Not.Null);
        Assert.That(attr!.UriTemplate, Is.EqualTo("nethermind://node/config"));
    }
}
