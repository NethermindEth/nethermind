// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;
using Nethermind.Mcp.Tools;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Test.Tools;

public class ToolsTests
{
    [Test]
    public void NodeStatusTools_get_sync_status_delegates_to_adapter()
    {
        INethermindNodeAdapter adapter = Substitute.For<INethermindNodeAdapter>();
        SyncStatusDto expected = new(CurrentBlock: 100, HighestKnownBlock: 150, SyncMode: "Syncing", BlocksBehind: 50, PeerCount: 7);
        adapter.GetSyncStatus().Returns(expected);

        NodeStatusTools tools = new(adapter);
        SyncStatusDto actual = tools.GetSyncStatus();

        Assert.That(actual, Is.SameAs(expected));
        adapter.Received(1).GetSyncStatus();
    }

    [Test]
    public void NodeStatusTools_get_node_version_delegates_to_adapter()
    {
        INethermindNodeAdapter adapter = Substitute.For<INethermindNodeAdapter>();
        NodeVersionDto expected = new(
            ClientVersion: "Nethermind/v1.0.0",
            DotNetRuntime: ".NET 10.0",
            OperatingSystem: "Linux",
            EnabledRpcModules: new[] { "eth", "net" });
        adapter.GetNodeVersion().Returns(expected);

        NodeStatusTools tools = new(adapter);
        NodeVersionDto actual = tools.GetNodeVersion();

        Assert.That(actual, Is.SameAs(expected));
        adapter.Received(1).GetNodeVersion();
    }

    [Test]
    public void NodeHealthTools_get_node_health_delegates_to_adapter()
    {
        INethermindNodeAdapter adapter = Substitute.For<INethermindNodeAdapter>();
        NodeHealthDto expected = new(
            OverallStatus: "Healthy",
            Checks: Array.Empty<HealthCheckDto>(),
            UptimeSeconds: 3600,
            ProcessMemoryMb: 1024,
            GcGen0Collections: 1,
            GcGen1Collections: 2,
            GcGen2Collections: 3,
            DiskFreeGb: null,
            DiskUsedGb: null);
        adapter.GetNodeHealth().Returns(expected);

        NodeHealthTools tools = new(adapter);
        NodeHealthDto actual = tools.GetNodeHealth();

        Assert.That(actual, Is.SameAs(expected));
        adapter.Received(1).GetNodeHealth();
    }

    [Test]
    public void ChainQueryTools_get_block_with_default_uses_latest()
    {
        INethermindNodeAdapter adapter = Substitute.For<INethermindNodeAdapter>();

        ChainQueryTools tools = new(adapter);
        tools.GetBlock();

        adapter.Received(1).GetBlock(Arg.Is<BlockParameter>(p => p.Type == BlockParameterType.Latest));
    }

    [Test]
    public void ChainQueryTools_get_block_by_number_passes_correct_BlockParameter()
    {
        INethermindNodeAdapter adapter = Substitute.For<INethermindNodeAdapter>();

        ChainQueryTools tools = new(adapter);
        tools.GetBlock("12345");

        adapter.Received(1).GetBlock(Arg.Is<BlockParameter>(p =>
            p.Type == BlockParameterType.BlockNumber && p.BlockNumber == 12345));
    }

    [Test]
    public void ChainQueryTools_get_block_by_hash_passes_correct_BlockParameter()
    {
        INethermindNodeAdapter adapter = Substitute.For<INethermindNodeAdapter>();
        const string hex = "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
        Hash256 expectedHash = new(hex);

        ChainQueryTools tools = new(adapter);
        tools.GetBlock(hex);

        adapter.Received(1).GetBlock(Arg.Is<BlockParameter>(p =>
            p.Type == BlockParameterType.BlockHash && p.BlockHash != null && p.BlockHash == expectedHash));
    }

    [Test]
    public void ChainQueryTools_get_block_throws_on_invalid_input()
    {
        INethermindNodeAdapter adapter = Substitute.For<INethermindNodeAdapter>();
        ChainQueryTools tools = new(adapter);

        Assert.Throws<ArgumentException>(() => tools.GetBlock("not-a-block-id"));
    }
}
