// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class NodeStorageCacheTests
{
    [Test]
    public async Task GetOrAdd_CoalescesConcurrentLoadsForTheSameNode()
    {
        NodeStorageCache cache = new() { Enabled = true };
        NodeKey key = new(null, TreePath.Empty, TestItem.KeccakA);
        byte[] expected = [1, 2, 3];
        int loadCount = 0;
        using ManualResetEventSlim firstLoadEntered = new(initialState: false);
        using ManualResetEventSlim duplicateLoadEntered = new(initialState: false);
        using ManualResetEventSlim startFollowers = new(initialState: false);
        using ManualResetEventSlim releaseLoad = new(initialState: false);
        using CountdownEvent followersReady = new(initialCount: 7);

        byte[] Load(in NodeKey _)
        {
            if (Interlocked.Increment(ref loadCount) == 1)
            {
                firstLoadEntered.Set();
            }
            else
            {
                duplicateLoadEntered.Set();
            }

            releaseLoad.Wait();
            return expected;
        }

        Task<byte[]?>[] loads = new Task<byte[]?>[8];
        loads[0] = Task.Factory.StartNew(
            () => cache.GetOrAdd(in key, Load),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.That(firstLoadEntered.Wait(5_000), Is.True, "the first load must enter the value factory");

        for (int i = 1; i < loads.Length; i++)
        {
            loads[i] = Task.Factory.StartNew(
                () =>
                {
                    followersReady.Signal();
                    startFollowers.Wait();
                    return cache.GetOrAdd(in key, Load);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        bool allFollowersReady = followersReady.Wait(5_000);
        startFollowers.Set();
        bool duplicateLoadStarted = duplicateLoadEntered.Wait(500);
        releaseLoad.Set();
        byte[]?[] results = await Task.WhenAll(loads);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allFollowersReady, Is.True);
            Assert.That(duplicateLoadStarted, Is.False, "followers must wait for the in-flight load instead of repeating it");
            Assert.That(loadCount, Is.EqualTo(1));
            foreach (byte[]? result in results)
            {
                Assert.That(result, Is.SameAs(expected));
            }
        }
    }
}
