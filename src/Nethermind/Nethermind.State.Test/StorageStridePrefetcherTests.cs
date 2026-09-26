// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class StorageStridePrefetcherTests
{
    [Test]
    public void OnRead_ToleratesNonIncreasingSlots()
    {
        using CancellationTokenSource cts = new();
        StorageStridePrefetcher prefetcher = new(
            () => EmptyStorageTree.Instance,
            new SeqlockCache<StorageCell, UInt256>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 4,
            tryReserveEngagement: static () => StorageStridePrefetcher.EngagementResult.Granted);

        Read(3);
        Read(2);
        Read(1);
        Read(0);

        UInt256 index = (UInt256)1 << 40;
        UInt256 stride = 7;
        for (int i = 0; i < 12; i++, index += stride)
        {
            Read(index);
        }

        Read(0);
        Read(1);
        Read(0);

        cts.Cancel();
        Assert.DoesNotThrow(() => prefetcher.Dispose());

        void Read(UInt256 slot) => Assert.DoesNotThrow(() => prefetcher.OnRead(in slot));
    }

    [Test]
    public void OnRead_EngagesForLowSlotStrides()
    {
        using CancellationTokenSource cts = new();
        SeqlockCache<StorageCell, UInt256> cache = new();
        StorageStridePrefetcher prefetcher = new(
            () => EmptyStorageTree.Instance,
            cache,
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 4,
            tryReserveEngagement: static () => StorageStridePrefetcher.EngagementResult.Granted);

        UInt256 index = 1;
        UInt256 stride = 1;
        for (int i = 0; i < 12; i++, index += stride)
        {
            prefetcher.OnRead(in index);
        }

        StorageCell farCell = new(TestItem.AddressA, 64);
        Assert.That(SpinWait.SpinUntil(() => cache.TryGetValue(in farCell, out _), 1000), Is.True);

        cts.Cancel();
        Assert.DoesNotThrow(() => prefetcher.Dispose());
    }

    [Test]
    public void HoldsReaderSlot_ReleasesWhenStarterTaskCompletes()
    {
        using CancellationTokenSource cts = new();
        int engagements = 0;
        using StorageStridePrefetcher prefetcher = new(
            () => EmptyStorageTree.Instance,
            new SeqlockCache<StorageCell, UInt256>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 1,
            tryReserveEngagement: () =>
            {
                engagements++;
                return StorageStridePrefetcher.EngagementResult.Granted;
            });

        try
        {
            UInt256 index = 1;
            for (int i = 0; i < 12; i++, index++)
                prefetcher.OnRead(in index);

            Assert.That(engagements, Is.EqualTo(1));
            Assert.That(SpinWait.SpinUntil(() => !prefetcher.HoldsReaderSlot, 5000), Is.True);
        }
        finally
        {
            cts.Cancel();
        }
    }

    [TestCase(false, 1)]
    [TestCase(true, 2)]
    public void OnRead_HandlesTerminalAndTransientEngagementRefusals(bool retryLater, int expectedAttempts)
    {
        using CancellationTokenSource cts = new();
        int engageAttempts = 0;
        int treeCreations = 0;
        StorageStridePrefetcher.EngagementResult firstResult = retryLater
            ? StorageStridePrefetcher.EngagementResult.RetryLater
            : StorageStridePrefetcher.EngagementResult.Exhausted;
        using StorageStridePrefetcher prefetcher = new(
            () =>
            {
                Interlocked.Increment(ref treeCreations);
                return EmptyStorageTree.Instance;
            },
            new SeqlockCache<StorageCell, UInt256>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 1,
            tryReserveEngagement: () =>
            {
                int attempt = Interlocked.Increment(ref engageAttempts);
                return attempt == 1 ? firstResult : StorageStridePrefetcher.EngagementResult.Granted;
            });

        try
        {
            UInt256 index = 1;
            for (int i = 0; i < 8; i++, index++)
                prefetcher.OnRead(in index);

            Assert.That(engageAttempts, Is.EqualTo(1));
            for (int i = 0; i < 6; i++, index++)
                prefetcher.OnRead(in index);

            Assert.That(engageAttempts, Is.EqualTo(1));
            prefetcher.OnRead(in index);
            Assert.That(engageAttempts, Is.EqualTo(expectedAttempts));
            if (retryLater)
            {
                Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref treeCreations) != 0, 5000), Is.True,
                    "a transient refusal should be retried after another matching run");
            }
            else
            {
                Assert.That(prefetcher.IsBroken, Is.True);
                Assert.That(Volatile.Read(ref treeCreations), Is.Zero);
            }
        }
        finally
        {
            cts.Cancel();
        }
    }

    [Test]
    public void Dispose_DoesNotThrowWhenLookaheadOverflowsUInt256()
    {
        using CancellationTokenSource cts = new();
        StorageStridePrefetcher prefetcher = new(
            () => EmptyStorageTree.Instance,
            new SeqlockCache<StorageCell, UInt256>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 4,
            tryReserveEngagement: static () => StorageStridePrefetcher.EngagementResult.Granted);

        UInt256 stride = 10;
        UInt256 start = UInt256.MaxValue - (stride * 7);
        for (int i = 0; i < 8; i++)
        {
            UInt256 index = start + (stride * (UInt256)(uint)i);
            prefetcher.OnRead(in index);
        }

        Thread.Sleep(50);
        cts.Cancel();

        Assert.DoesNotThrow(() => prefetcher.Dispose());
    }

    [Test]
    public void A_detector_that_never_engaged_holds_no_reader_slot()
    {
        using CancellationTokenSource cts = new();
        int engagements = 0;
        StorageStridePrefetcher prefetcher = new(
            () => EmptyStorageTree.Instance,
            new SeqlockCache<StorageCell, UInt256>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 1,
            tryReserveEngagement: () => { engagements++; return StorageStridePrefetcher.EngagementResult.Granted; });

        // Off-pattern reads: an ordinary contract that touches storage without striding. It never engages,
        // so it never breaks either - and it must still not count against the owner's concurrency cap,
        // otherwise the first contracts to touch storage lock every slot for the whole block.
        UInt256 index = 1;
        for (int i = 0; i < 32; i++)
        {
            prefetcher.OnRead(in index);
            index += (UInt256)(uint)(i + 1);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(engagements, Is.Zero, "an off-pattern run must not engage");
            Assert.That(prefetcher.IsBroken, Is.False, "a detector that never engaged never breaks");
            Assert.That(prefetcher.HoldsReaderSlot, Is.False, "so it owns no reader threads and no slot");
        }

        Assert.DoesNotThrow(() => prefetcher.Dispose());
    }

    [Test]
    public void StopAndGetReaders_WaitsForAnInFlightPublish()
    {
        using CancellationTokenSource cts = new();
        StorageStridePrefetcher prefetcher = new(
            () => EmptyStorageTree.Instance,
            new SeqlockCache<StorageCell, UInt256>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 1,
            tryReserveEngagement: static () => StorageStridePrefetcher.EngagementResult.Granted);

        // Stand in for a reader that has passed the seal check and is about to write to the cache.
        // Teardown must not return until that write has landed: the owner clears the cache for the
        // next block right after, so a write completing later publishes parent state into it.
        Assert.That(prefetcher.TryBeginPublish(), Is.True);

        Thread teardown = new(() => prefetcher.StopAndGetReaders()) { IsBackground = true };
        teardown.Start();

        Assert.That(SpinWait.SpinUntil(() => prefetcher.IsBroken, 5000), Is.True, "teardown never started");
        Assert.That(teardown.Join(200), Is.False, "teardown returned while a publish was in flight");

        prefetcher.EndPublish();
        Assert.That(teardown.Join(5000), Is.True);
        Assert.That(prefetcher.TryBeginPublish(), Is.False, "publishing must stay sealed after teardown");
    }

    [Test]
    public void OnRead_DoesNotEngageWithoutAnEngagementBudget()
    {
        using CancellationTokenSource cts = new();
        SeqlockCache<StorageCell, UInt256> cache = new();
        int engageAttempts = 0;
        int treeCreations = 0;
        StorageStridePrefetcher prefetcher = new(
            () =>
            {
                Interlocked.Increment(ref treeCreations);
                return EmptyStorageTree.Instance;
            },
            cache,
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 4,
            tryReserveEngagement: () =>
            {
                engageAttempts++;
                return StorageStridePrefetcher.EngagementResult.Exhausted;
            });

        UInt256 index = 1;
        UInt256 stride = 1;
        for (int i = 0; i < 32; i++, index += stride)
        {
            prefetcher.OnRead(in index);
        }

        Thread.Sleep(50);

        using (Assert.EnterMultipleScope())
        {
            // A refused engagement disengages the detector for good, so the block's budget cannot be
            // re-probed once per read for the rest of the scan.
            Assert.That(engageAttempts, Is.EqualTo(1));
            Assert.That(prefetcher.IsBroken, Is.True);
            Assert.That(treeCreations, Is.Zero, "no reader may start without an engagement budget");
        }

        StorageCell farCell = new(TestItem.AddressA, 64);
        Assert.That(cache.TryGetValue(in farCell, out _), Is.False);

        Assert.DoesNotThrow(() => prefetcher.Dispose());
    }

    /// <remarks>
    /// Drives the reader cap of <see cref="PrewarmerScopeProvider"/> over a substituted backend, so it does not
    /// depend on a storage layout.
    /// </remarks>
    [TestCase(true)]
    [TestCase(false)]
    public void PrewarmerScope_EngagesDetectorCreatedWhileReadersHoldSlots(bool attemptBeforeSlotFree)
    {
        ControlledStorageTrees controlledTrees = new();
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        baseProvider.SupportsConcurrentScopes.Returns(true);
        baseProvider.TryBeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope>()).Returns(call => call.Succeed(2, baseScope));
        baseScope.CreateStorageTree(Arg.Any<Address>())
            .Returns(callInfo => controlledTrees.Get(callInfo.Arg<Address>()));

        PreBlockCaches caches = new(TestPreBlockCachesConfig.Small);
        PrewarmerScopeProvider prewarmer = new(
            baseProvider,
            new PrewarmerState(caches, isPrewarmer: false),
            LimboLogs.Instance);

        using IWorldStateScopeProvider.IScope scope = prewarmer.BeginScope(null, new LocalMetrics());
        try
        {
            controlledTrees.OwnerThreadId = Environment.CurrentManagedThreadId;
            controlledTrees.HoldReaders();

            IWorldStateScopeProvider.IStorageTree[] initialTrees = new IWorldStateScopeProvider.IStorageTree[4];
            for (int i = 0; i < initialTrees.Length; i++)
            {
                Address address = new(Keccak.Compute($"stride-held-{i}"));
                initialTrees[i] = scope.CreateStorageTree(address);
                ReadStride(initialTrees[i], startIndex: 1, count: 12);
            }

            Assert.That(controlledTrees.WaitForReaderTrees(initialTrees.Length), Is.True,
                "The initial detectors did not retain their reader slots.");

            Address lateAddress = new(Keccak.Compute("stride-late"));
            IWorldStateScopeProvider.IStorageTree lateTree = scope.CreateStorageTree(lateAddress);

            if (attemptBeforeSlotFree)
                ReadStride(lateTree, startIndex: 1, count: 8);

            BreakStride(initialTrees[0]);
            ReadStride(lateTree, startIndex: attemptBeforeSlotFree ? 9 : 1, count: 12);

            Assert.That(controlledTrees.WaitForReaderTrees(initialTrees.Length + 1), Is.True,
                "The detector created while all slots were occupied did not engage after a slot was freed.");

            controlledTrees.ReleaseReaders();
            StorageCell lateFarCell = new(lateAddress, 64);
            Assert.That(SpinWait.SpinUntil(() => caches.StorageCache.TryGetValue(in lateFarCell, out _), 5000), Is.True,
                "The late detector did not warm a slot after the earlier detector released its slot.");

            static void ReadStride(IWorldStateScopeProvider.IStorageTree storage, int startIndex, int count)
            {
                UInt256 index = (UInt256)(uint)startIndex;
                for (int i = 0; i < count; i++, index++)
                    storage.Get(in index, out _);
            }

            static void BreakStride(IWorldStateScopeProvider.IStorageTree storage)
            {
                UInt256 index = 10_000;
                for (int i = 0; i < 16; i++, index += (UInt256)(101 + i * i))
                    storage.Get(in index, out _);
            }
        }
        finally
        {
            controlledTrees.ReleaseReaders();
        }
    }

    private sealed class ControlledStorageTrees
    {
        private readonly ConcurrentDictionary<Address, ControlledStorageTree> _trees = new();
        private readonly TaskCompletionSource<bool> _releaseReaders = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ownerThreadId;
        private bool _holdReaders;

        public int OwnerThreadId
        {
            set => Volatile.Write(ref _ownerThreadId, value);
        }

        public IWorldStateScopeProvider.IStorageTree Get(Address address) =>
            _trees.GetOrAdd(address, _ => new ControlledStorageTree(this));

        public void HoldReaders() => Volatile.Write(ref _holdReaders, true);

        public void ReleaseReaders()
        {
            Volatile.Write(ref _holdReaders, false);
            _releaseReaders.TrySetResult(true);
        }

        public bool WaitForReaderTrees(int count) => SpinWait.SpinUntil(() =>
        {
            int entered = 0;
            foreach (KeyValuePair<Address, ControlledStorageTree> entry in _trees)
            {
                if (entry.Value.HasReaderEntered) entered++;
            }

            return entered >= count;
        }, 5000);

        private sealed class ControlledStorageTree(ControlledStorageTrees owner) : IWorldStateScopeProvider.IStorageTree
        {
            private readonly ControlledStorageTrees _owner = owner;
            private int _readerEntered;

            public bool HasReaderEntered => Volatile.Read(ref _readerEntered) != 0;

            public Hash256 RootHash => Keccak.EmptyTreeHash;

            public void Get(in UInt256 index, out UInt256 value)
            {
                if (Environment.CurrentManagedThreadId != Volatile.Read(ref _owner._ownerThreadId)
                    && Volatile.Read(ref _owner._holdReaders))
                {
                    Volatile.Write(ref _readerEntered, 1);
                    _owner._releaseReaders.Task.GetAwaiter().GetResult();
                }

                value = 1;
            }

            public void HintSet(in UInt256 index) { }
        }
    }

    private sealed class EmptyStorageTree : IWorldStateScopeProvider.IStorageTree
    {
        public static EmptyStorageTree Instance { get; } = new();

        public Hash256 RootHash => Keccak.EmptyTreeHash;

        public void Get(in UInt256 index, out UInt256 value) => value = default;

        public void HintSet(in UInt256 index) { }

        public byte[] Get(in ValueHash256 hash) => [];
    }
}
