// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public partial class FlatStorageTreeDeleteWarmupTests
{
    [Test]
    public async Task QueuedStorageDeletes_WithDeleteFlags_WarmCollapseSiblingsBeforeWrites()
    {
        using Fixture fixture = new();
        UInt256[] slots = fixture.PickSlots();
        await using TrieWarmer warmer = new(LimboLogs.Instance, new FlatDbConfig());
        (long missingSiblingLoads, Hash256 unflaggedRoot) = QueuedDeleteLoads(fixture, warmer, slots, false);
        (long warmedLoads, Hash256 flaggedRoot) = QueuedDeleteLoads(fixture, warmer, slots, true);
        Hash256 expectedRoot = fixture.ExpectedRootWithout(slots);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingSiblingLoads, Is.GreaterThan(warmedLoads + CollisionAllowance),
                "the queued delete flag must warm the collapse siblings that path-only warming leaves cold");
            Assert.That(flaggedRoot, Is.EqualTo(expectedRoot), "queued batch warmup must preserve the delete root");
            Assert.That(unflaggedRoot, Is.EqualTo(expectedRoot), "the scalar read fallback must produce the same delete root");
        }
    }

    private static (long Loads, Hash256 Root) QueuedDeleteLoads(Fixture fixture, TrieWarmer warmer, UInt256[] slots, bool flag)
    {
        (FlatWorldStateScope scope, CountingReader reader) = fixture.OpenScope(passDeleteFlag: false);
        try
        {
            FlatStorageTree storage = (FlatStorageTree)scope.CreateStorageTree(Contract);
            Type warmerType = typeof(TrieWarmer);
            Type jobType = warmerType.GetNestedType("Job", BindingFlags.NonPublic)!;
            object queue = warmerType.GetField("_jobBufferMultiThreaded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(warmer)!;
            MethodInfo enqueue = queue.GetType().GetMethod("TryEnqueue")!;
            foreach (UInt256 slot in slots)
            {
                object job = Activator.CreateInstance(jobType, [storage, null, slot, scope.HintSequenceId, flag])!;
                scope.IncrementOutstandingWarmups();
                Assert.That(enqueue.Invoke(queue, [job]), Is.True, "precondition: the isolated queue must accept every slot");
            }
            MethodInfo dequeue = warmerType.GetMethod("TryDequeue", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo handle = warmerType.GetMethod("HandleJobs", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object?[] arguments = [null];
            while ((bool)dequeue.Invoke(warmer, arguments)!) handle.Invoke(warmer, [arguments[0]]);
            Assert.That(scope.OutstandingWarmups, Is.Zero, "the queue must complete every storage hint before writes start");

            long before = reader.Loads;
            using (IWorldStateScopeProvider.IStorageWriteBatch writes = storage.CreateWriteBatch(slots.Length, static (_, _) => { }))
                foreach (UInt256 slot in slots) writes.Set(slot, UInt256.Zero);
            return (reader.Loads - before, storage.RootHash);
        }
        finally
        {
            scope.CompleteWarmups(scope.OutstandingWarmups);
            scope.Dispose();
        }
    }
}
