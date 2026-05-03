// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;
using Nethermind.Synchronization.Peers;
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

    [Test]
    public void GetSyncStatus_returns_current_and_best_known_block_with_peer_count()
    {
        INethermindApi api = Substitute.For<INethermindApi>();
        BlockHeader head = Build.A.Block.WithNumber(100).TestObject.Header;
        BlockHeader bestSuggested = Build.A.Block.WithNumber(150).TestObject.Header;
        api.BlockTree!.Head.Returns(Build.A.Block.WithHeader(head).TestObject);
        api.BlockTree!.BestSuggestedHeader.Returns(bestSuggested);
        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        syncPeerPool.PeerCount.Returns(7);
        api.SyncPeerPool.Returns(syncPeerPool);

        INethermindNodeAdapter adapter = new NethermindNodeAdapter(api);
        SyncStatusDto status = adapter.GetSyncStatus();

        Assert.That(status.CurrentBlock, Is.EqualTo(100));
        Assert.That(status.HighestKnownBlock, Is.EqualTo(150));
        Assert.That(status.BlocksBehind, Is.EqualTo(50));
        Assert.That(status.PeerCount, Is.EqualTo(7));
        Assert.That(status.SyncMode, Is.Not.Empty);
    }
}
