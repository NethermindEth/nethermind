// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class DependentItemTests
{
    [Test]
    public void Concurrent_resolutions_complete_the_item_exactly_once()
    {
        const int dependencyCount = 16;
        const int rounds = 2000;

        for (int round = 0; round < rounds; round++)
        {
            DependentItem item = new(new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.State), [], dependencyCount);
            int completions = 0;

            Parallel.For(0, dependencyCount, _ =>
            {
                if (item.ResolveDependency()) Interlocked.Increment(ref completions);
            });

            Assert.That(completions, Is.EqualTo(1), $"round {round}");
        }
    }
}
