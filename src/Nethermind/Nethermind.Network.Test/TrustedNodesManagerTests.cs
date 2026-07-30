// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class TrustedNodesManagerTests
{
    private const string EnodeString =
        "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@192.81.208.223:30303";

    private const string EnodeStringSamePubkeyDifferentAddress =
        "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@10.0.0.1:31313";

    [Test]
    public async Task RemoveAsync_WhenSaveFileCancelled_StillFiresNodeRemoved()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"test-trusted-nodes-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "[]");
        TrustedNodesManager manager = new(path, LimboLogs.Instance);
        Enode enode = new(EnodeString);
        await manager.AddAsync(enode, updateFile: false);

        bool nodeRemovedFired = false;
        manager.NodeRemoved += (_, _) => nodeRemovedFired = true;

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Assert.That(async () => await manager.RemoveAsync(enode, updateFile: true, cts.Token), Throws.InstanceOf<System.OperationCanceledException>(), "the pre-cancelled token must abort SaveFileAsync");

        Assert.That(nodeRemovedFired, Is.True, "NodeRemoved must fire before the file write, so a cancelled SaveFileAsync cannot leave the peer untrusted-in-memory yet still connected via the missed disconnect event chain");
        Assert.That(manager.Nodes, Has.None.Matches<NetworkNode>(n => n.NodeId == enode.PublicKey), "the in-memory dict must already be cleared before the cancellation point, so an aborted file write cannot leave the peer trusted in memory");
    }

    [Test]
    public async Task IsTrusted_AfterAdd_ReturnsTrueRegardlessOfHostOrPort()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"test-trusted-nodes-{System.Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "[]");
        TrustedNodesManager manager = new(path, LimboLogs.Instance);
        Enode enodeOriginal = new(EnodeString);
        await manager.AddAsync(enodeOriginal, updateFile: false);

        Assert.That(manager.IsTrusted(enodeOriginal), Is.True, "precondition: the peer was just added");

        Enode enodeSamePubkeyDifferentAddress = new(EnodeStringSamePubkeyDifferentAddress);
        Assert.That(manager.IsTrusted(enodeSamePubkeyDifferentAddress), Is.True, "trust is keyed by public key only, matching geth's Server.trusted map (p2p/server.go:644 trusted[enode.ID()]); a trusted peer at a different address is still trusted");
    }
}
