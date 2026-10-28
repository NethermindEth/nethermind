// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;
using CoreCollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Store.Test;

public partial class StorageProviderTests
{
    [Test]
    public void Commit_ReadOnlyRounds_ReusesPendingBuffer([Values(2, 2048)] int slotCount)
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Get(new StorageCell(ctx.Address1, UInt256.Zero), out _);
        provider.Commit(Frontier.Instance);
        object buffer = null;
        for (int round = 0; round < 3; round++)
        {
            for (int i = 1; i <= slotCount; i++)
            {
                provider.Get(new StorageCell(ctx.Address1, (UInt256)i), out _);
            }

            object currentBuffer = GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues");
            buffer ??= currentBuffer;
            Assert.That(currentBuffer, Is.SameAs(buffer));
            provider.Commit(Frontier.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.SameAs(buffer));
                Assert.That(((System.Collections.ICollection)buffer).Count, Is.Zero);
            }
        }
    }

    [Test]
    public void Commit_OversizedPendingBuffer_ReleasesBuffer()
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Get(new StorageCell(ctx.Address1, UInt256.Zero), out _);
        provider.Commit(Frontier.Instance);
        for (int i = 1; i <= CoreCollectionExtensions.DefaultTrimAboveCapacity + 1; i++)
        {
            provider.Get(new StorageCell(ctx.Address1, (UInt256)i), out _);
        }

        Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.Not.Null);
        provider.Commit(Frontier.Instance);
        Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.Null);
    }

    [Test]
    public void EndScope_PendingBuffer_ReleasedOnlyWhenGrown([Values(1, PersistentStorageProvider.RetainedPendingCapacity + 1)] int slotCount)
    {
        using Context ctx = new(UseFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        using (provider.BeginScope(IWorldState.PreGenesis))
        {
            provider.Get(new StorageCell(TestItem.AddressA, UInt256.Zero), out _);
            provider.Commit(Frontier.Instance);
            for (int i = 1; i <= slotCount; i++)
            {
                provider.Get(new StorageCell(TestItem.AddressA, (UInt256)i), out _);
            }

            Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.Not.Null);
        }

        bool grown = slotCount > PersistentStorageProvider.RetainedPendingCapacity;
        Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), grown ? Is.Null : Is.Not.Null);
    }

    /// <remarks>Only debug builds report an undisposed list, from its finalizer, so release builds pass trivially.</remarks>
    [Test]
    [NonParallelizable]
    public void EndScope_KeptPendingBuffer_IsNotReportedAsLeaked()
    {
        TextWriter stderr = Console.Error;
        using StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            KeepPendingBufferAndDropProvider();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally
        {
            Console.SetError(stderr);
        }

        Assert.That(captured.ToString(), Does.Not.Contain(nameof(PersistentStorageProvider)));

        [MethodImpl(MethodImplOptions.NoInlining)]
        void KeepPendingBufferAndDropProvider()
        {
            using Context ctx = new(UseFlat, setInitialState: false);
            WorldState provider = BuildStorageProvider(ctx);
            using (provider.BeginScope(IWorldState.PreGenesis))
            {
                provider.Get(new StorageCell(TestItem.AddressA, UInt256.Zero), out _);
            }

            Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.Not.Null);
        }
    }

    private const int LargeTransactionSlots = 3_000;

    [Test]
    public void Sparse_commits_after_a_large_transaction_shrink_the_round_maps()
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        object journalHeads = GetPrivateField(provider._persistentStorageProvider, "_intraBlockCache");
        WriteLargeTransaction(provider, ctx.Address1);
        int grown = GetCollectionCapacity(journalHeads);

        CommitSparseTransactions(provider, ctx.Address1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(grown, Is.GreaterThan(LargeTransactionSlots));
            Assert.That(GetCollectionCapacity(journalHeads), Is.LessThan(SparseClearTrimmer.MinCapacity * 2));
            Assert.That(GetCollectionCapacity(GetPrivateField(provider._persistentStorageProvider, "_originalValues")), Is.LessThan(SparseClearTrimmer.MinCapacity * 2));
        }
    }

    [Test]
    public void Revert_after_the_round_maps_shrink_restores_values_and_originals()
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        WriteLargeTransaction(provider, ctx.Address1);
        CommitSparseTransactions(provider, ctx.Address1);
        StorageCell written = new(ctx.Address1, 1);
        StorageCell reverted = new(ctx.Address1, 2);
        StorageCell created = new(ctx.Address1, LargeTransactionSlots * 2);

        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(written, out _);
        Snapshot beforeWrites = provider.TakeSnapshot();
        provider.Set(written, Value(5));
        provider.Set(reverted, Value(6));
        provider.Set(created, Value(9));
        provider.Restore(beforeWrites);
        provider.Set(written, Value(8));
        provider.GetOriginal(in written, out UInt256 original);
        ReadCollectingStorageTracer tracer = new();
        provider.Commit(Frontier.Instance, tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(original, Is.EqualTo(Value(1)), "the original is the value the large transaction committed");
            Assert.That(ReadSlot(provider, written), Is.EqualTo(Value(8)));
            Assert.That(ReadSlot(provider, reverted), Is.EqualTo(Value(1)), "a reverted write must not survive the commit");
            Assert.That(ReadSlot(provider, created), Is.EqualTo(UInt256.Zero), "a reverted new slot must stay empty");
            Assert.That(tracer.Changes, Has.Count.EqualTo(1));
            Assert.That(tracer.Changes[0].Cell, Is.EqualTo(written));
            Assert.That(tracer.Changes[0].Before, Is.EqualTo(_values[1]));
            Assert.That(tracer.Changes[0].After, Is.EqualTo(_values[8]));
        }
    }

    [Test]
    public void Destroy_after_the_round_maps_shrink_clears_the_large_transaction_storage([Values] bool revertClear)
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        WriteLargeTransaction(provider, ctx.Address1);
        CommitSparseTransactions(provider, ctx.Address1);
        StorageCell first = new(ctx.Address1, 0);
        StorageCell last = new(ctx.Address1, LargeTransactionSlots - 1);

        provider.TakeSnapshot(newTransactionStart: true);
        Snapshot beforeClear = provider.TakeSnapshot();
        provider.ClearStorage(ctx.Address1);
        Assert.That(ReadSlot(provider, last), Is.EqualTo(UInt256.Zero), "a cleared slot reads zero before the commit");
        if (revertClear)
        {
            provider.Restore(beforeClear);
        }
        else
        {
            provider.MarkStorageDestroyed(ctx.Address1);
        }

        provider.Commit(Frontier.Instance, new ReadCollectingStorageTracer());
        UInt256 expected = revertClear ? Value(1) : UInt256.Zero;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadSlot(provider, first), Is.EqualTo(expected));
            Assert.That(ReadSlot(provider, last), Is.EqualTo(expected));
        }

        provider.Set(first, Value(3));
        provider.Commit(Frontier.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadSlot(provider, first), Is.EqualTo(Value(3)), "a write after the destroy must survive");
            Assert.That(ReadSlot(provider, last), Is.EqualTo(expected));
        }
    }

    [Test]
    public void Originals_after_the_round_maps_shrink_follow_each_transaction()
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        WriteLargeTransaction(provider, ctx.Address1);
        CommitSparseTransactions(provider, ctx.Address1);
        StorageCell cell = new(ctx.Address1, 7);

        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell, out _);
        provider.Set(cell, Value(2));
        provider.Set(cell, Value(4));
        provider.GetOriginal(in cell, out UInt256 firstOriginal);
        provider.Commit(Frontier.Instance, new ReadCollectingStorageTracer());

        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell, out _);
        provider.GetOriginal(in cell, out UInt256 secondOriginal);
        provider.Set(cell, Value(6));
        provider.GetOriginal(in cell, out UInt256 secondOriginalAfterWrite);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstOriginal, Is.EqualTo(Value(1)));
            Assert.That(secondOriginal, Is.EqualTo(Value(4)), "the next transaction starts from the committed value");
            Assert.That(secondOriginalAfterWrite, Is.EqualTo(Value(4)));
        }
    }

    /// <summary>Writes far more slots than the later rounds, so the round maps grow well past what those rounds use.</summary>
    private void WriteLargeTransaction(WorldState provider, Address address)
    {
        provider.TakeSnapshot(newTransactionStart: true);
        for (int i = 0; i < LargeTransactionSlots; i++)
        {
            StorageCell cell = new(address, (UInt256)i);
            provider.Get(cell, out _);
            provider.Set(cell, Value(1));
        }

        provider.Commit(Frontier.Instance, new ReadCollectingStorageTracer());
    }

    /// <summary>Commits enough small traced transactions for the sparse rounds to shrink the round maps.</summary>
    private static void CommitSparseTransactions(WorldState provider, Address address)
    {
        for (int round = 0; round < SparseClearTrimmer.SparseClearsBeforeTrim; round++)
        {
            provider.TakeSnapshot(newTransactionStart: true);
            StorageCell cell = new(address, (UInt256)(LargeTransactionSlots + round % 3));
            provider.Get(cell, out _);
            provider.Set(cell, (UInt256)(round + 1));
            provider.Commit(Frontier.Instance, new ReadCollectingStorageTracer());
        }
    }
}
