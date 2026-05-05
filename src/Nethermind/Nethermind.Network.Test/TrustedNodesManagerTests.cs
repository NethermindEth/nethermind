// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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

        await FluentActions.Awaiting(() => manager.RemoveAsync(enode, updateFile: true, cts.Token))
            .Should().ThrowAsync<System.OperationCanceledException>(because: "the pre-cancelled token must abort SaveFileAsync");

        nodeRemovedFired.Should().BeTrue(
            because: "NodeRemoved must fire before the file write, so a cancelled SaveFileAsync cannot leave the peer untrusted-in-memory yet still connected via the missed disconnect event chain");
    }
}
