// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
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
            new SeqlockCache<StorageCell, byte[]>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 4);

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
    public void OnRead_DoesNotEngageForLowSlotStrides()
    {
        using CancellationTokenSource cts = new();
        int treeCreations = 0;
        StorageStridePrefetcher prefetcher = new(
            () =>
            {
                Interlocked.Increment(ref treeCreations);
                return EmptyStorageTree.Instance;
            },
            new SeqlockCache<StorageCell, byte[]>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 4);

        UInt256 index = 1;
        UInt256 stride = 1;
        for (int i = 0; i < 12; i++, index += stride)
        {
            prefetcher.OnRead(in index);
        }

        Thread.Sleep(50);
        Assert.That(Volatile.Read(ref treeCreations), Is.Zero);

        cts.Cancel();
        Assert.DoesNotThrow(() => prefetcher.Dispose());
    }

    [Test]
    public void OnRead_EngagesForBulkSlotStrides()
    {
        using CancellationTokenSource cts = new();
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageStridePrefetcher prefetcher = new(
            () => EmptyStorageTree.Instance,
            cache,
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 4);

        UInt256 index = (UInt256)1 << 40;
        UInt256 stride = 1;
        for (int i = 0; i < 12; i++, index += stride)
        {
            prefetcher.OnRead(in index);
        }

        StorageCell farCell = new(TestItem.AddressA, ((UInt256)1 << 40) + 64);
        Assert.That(SpinWait.SpinUntil(() => cache.TryGetValue(in farCell, out _), 1000), Is.True);

        cts.Cancel();
        Assert.DoesNotThrow(() => prefetcher.Dispose());
    }

    [Test]
    public void Dispose_DoesNotThrowWhenLookaheadOverflowsUInt256()
    {
        using CancellationTokenSource cts = new();
        StorageStridePrefetcher prefetcher = new(
            () => EmptyStorageTree.Instance,
            new SeqlockCache<StorageCell, byte[]>(),
            TestItem.AddressA,
            cts.Token,
            readerConcurrency: 4);

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

    private sealed class EmptyStorageTree : IWorldStateScopeProvider.IStorageTree
    {
        public static EmptyStorageTree Instance { get; } = new();

        public Hash256 RootHash => Keccak.EmptyTreeHash;

        public byte[] Get(in UInt256 index) => [];

        public void HintSet(in UInt256 index, byte[] value) { }

        public byte[] Get(in ValueHash256 hash) => [];
    }
}
