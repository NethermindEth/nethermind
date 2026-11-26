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
        const int threadCount = 4;
        const int resolutionsPerThread = 100_000;

        DependentItem item = new(new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.State), [], threadCount * resolutionsPerThread);
        using Barrier start = new(threadCount);
        int completions = 0;

        Task[] resolvers = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            resolvers[t] = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < resolutionsPerThread; i++)
                {
                    if (item.ResolveDependency()) Interlocked.Increment(ref completions);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(resolvers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completions, Is.EqualTo(1));
            Assert.That(item.Counter, Is.Zero);
        }
    }
}
