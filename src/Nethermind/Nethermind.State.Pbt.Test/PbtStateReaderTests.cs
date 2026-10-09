// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.State.Pbt.Persistence;
using Nethermind.Pbt;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Snapshot;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtStateReaderTests
{
    [Test]
    public void UnavailableReadsPreserveTheTypedCause(
        [Values] bool storage,
        [Values] bool nullHeader,
        [Values("missing", "unavailable", "notRetained")] string failure,
        [Values] bool overridable)
    {
        IPbtDbManager manager = Substitute.For<IPbtDbManager>();
        using MemDb codeDb = new();
        using PbtOverridableWorldScope overridableScope = new(codeDb, manager, PooledRefCountingMemoryProvider.Instance, new PbtConfig(), UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
        IStateReader reader = overridable ? overridableScope.GlobalStateReader : new PbtStateReader(codeDb, manager);
        BlockHeader? header = nullHeader ? null : Build.A.BlockHeader.WithNumber(9).WithStateRoot(TestItem.KeccakA).TestObject;
        StateId stateId = new(header);
        StateUnavailableException? cause = failure switch
        {
            "unavailable" => new StateUnavailableException("untrusted state"),
            "notRetained" => new StateNotRetainedException("pruned state"),
            _ => null
        };
        manager.TryGatherReadOnlyBundle(stateId).Returns(_ => cause is null ? null : throw cause);
        manager.TryGatherBundle(stateId, Arg.Any<PbtSnapshotPooledList>(), Arg.Any<PbtResourcePool.Usage>()).Returns(_ => cause is null ? null : throw cause);

        MissingTrieNodeException? exception = Assert.Throws<MissingTrieNodeException>(() => Read(reader, header, storage));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Is.EqualTo($"State for block {header?.Number} is unavailable"));
            Assert.That(exception.Address, Is.Null);
            Assert.That(exception.Path, Is.EqualTo(TreePath.Empty));
            Assert.That(exception.Hash, Is.EqualTo(header?.StateRoot ?? Keccak.EmptyTreeHash));
            if (cause is null)
                Assert.That(exception.InnerException, Is.TypeOf<StateNotRetainedException>());
            else
                Assert.That(exception.InnerException, Is.SameAs(cause));
        }
    }

    [Test]
    public async Task Production_reader_prune_after_availability_check_preserves_unavailable_cause([Values] bool storage)
    {
        await using IContainer container = PbtTestContext.BuildProductionContainer(new PbtConfig { EnableLongFinality = false, CompactionOffset = 0 });
        StateId baseState = new(0, TestItem.KeccakA.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = container.Resolve<IPbtPersistence>().CreateWriteBatch(StateId.PreGenesis, baseState, TestItem.KeccakB.ValueHash256, WriteFlags.None)) batch.Commit();
        IPbtDbManager manager = container.Resolve<IPbtDbManager>();
        IPbtResourcePool pool = container.Resolve<IPbtResourcePool>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakC).TestObject;
        PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        content.Accounts[PbtStateKey.AddressKeyHash(TestItem.AddressA)] = PbtAccount.From(new Account(1, 100), null);
        container.Resolve<PbtSnapshotRepository>().TryAdd(new(baseState, new(header), TestItem.KeccakD.ValueHash256,
            content, pool, PbtResourcePool.Usage.MainBlockProcessing));
        PbtStateReader reader = container.Resolve<PbtStateReader>();
        using PbtReadOnlySnapshotBundle held = manager.GatherReadOnlyBundle(new(header));
        Assert.That(reader.HasStateForBlock(header), Is.True);

        manager.DropStateNotReachableFrom(baseState);
        MissingTrieNodeException? exception = Assert.Throws<MissingTrieNodeException>(() => Read(reader, header, storage));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.InnerException, Is.TypeOf<StateNotRetainedException>());
            Assert.That(reader.HasStateForBlock(header), Is.False);
            Assert.That(held.GetAccount(TestItem.AddressA)!.Value.ToAccount().Balance, Is.EqualTo((UInt256)100));
        }
    }

    [Test]
    public void StateRemovedAfterAvailabilityCheckIsNotAnAbsentEntry([Values] bool storage)
    {
        IPbtDbManager manager = Substitute.For<IPbtDbManager>();
        using MemDb codeDb = new();
        PbtStateReader reader = new(codeDb, manager);
        BlockHeader header = Build.A.BlockHeader.WithNumber(9).WithStateRoot(TestItem.KeccakA).TestObject;
        StateId stateId = new(header);
        manager.HasStateForBlock(stateId).Returns(true);
        Assert.That(reader.HasStateForBlock(header), Is.True);
        manager.HasStateForBlock(stateId).Returns(false);
        manager.TryGatherReadOnlyBundle(stateId).Returns((PbtReadOnlySnapshotBundle?)null);

        MissingTrieNodeException? exception = Assert.Throws<MissingTrieNodeException>(() => Read(reader, header, storage));

        Assert.That(exception!.InnerException, Is.TypeOf<StateNotRetainedException>());
    }

    [Test]
    public async Task AbsentEntriesInAvailableStateKeepTheirDefaultValues([Values] bool storage)
    {
        await using PbtTestContext ctx = new();
        using IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null);
        Hash256 root = scope.CommitBlock(1, TestItem.AddressA, 100, 1);
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithStateRoot(root).TestObject;

        if (storage)
            Assert.That(ctx.StateReader.GetStorage(header, TestItem.AddressB, 1), Is.EqualTo(UInt256.Zero));
        else
            Assert.That(ctx.StateReader.TryGetAccount(header, TestItem.AddressB, out _), Is.False);
    }

    [Test]
    public void TransientGatherFailureIsNotReclassified([Values] bool storage)
    {
        IPbtDbManager manager = Substitute.For<IPbtDbManager>();
        using MemDb codeDb = new();
        PbtStateReader reader = new(codeDb, manager);
        BlockHeader header = Build.A.BlockHeader.WithNumber(9).WithStateRoot(TestItem.KeccakA).TestObject;
        InvalidOperationException cause = new("gather retry exhausted");
        manager.TryGatherReadOnlyBundle(new StateId(header)).Returns(_ => throw cause);

        Assert.That(Assert.Throws<InvalidOperationException>(() => Read(reader, header, storage)), Is.SameAs(cause));
    }

    private static void Read(IStateReader reader, BlockHeader? header, bool storage)
    {
        if (storage) reader.GetStorage(header, TestItem.AddressA, UInt256.One, out _);
        else reader.TryGetAccount(header, TestItem.AddressA, out _);
    }

    [Test]
    public async Task HistoricalReadsSpanInMemoryLayersAndPersistence()
    {
        await using PbtTestContext ctx = new();
        Address address = TestItem.AddressA;

        Hash256[] roots = new Hash256[5];
        using IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null);
        for (ulong number = 1; number <= 4; number++)
        {
            // persist blocks 1-2 to disk, then keep blocks 3-4 in memory only
            if (number == 3) ctx.Manager.FlushCache(default);
            roots[number] = scope.CommitBlock(number, address, number * 100, 1);
        }

        // persisted floor read
        BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).WithStateRoot(roots[2]).TestObject;
        Assert.That(ctx.StateReader.HasStateForBlock(header2), Is.True);
        Assert.That(ctx.StateReader.TryGetAccount(header2, address, out AccountStruct accountAt2), Is.True);
        Assert.That(accountAt2.Balance, Is.EqualTo((UInt256)200));
        Assert.That(ctx.StateReader.GetStorage(header2, address, 1), Is.EqualTo((UInt256)2));

        // in-memory layered read above the floor
        BlockHeader header3 = Build.A.BlockHeader.WithNumber(3).WithStateRoot(roots[3]).TestObject;
        BlockHeader header4 = Build.A.BlockHeader.WithNumber(4).WithStateRoot(roots[4]).TestObject;
        Assert.That(ctx.StateReader.TryGetAccount(header3, address, out AccountStruct accountAt3), Is.True);
        Assert.That(accountAt3.Balance, Is.EqualTo((UInt256)300));
        Assert.That(ctx.StateReader.GetStorage(header4, address, 1), Is.EqualTo((UInt256)4));

        BlockHeader unknown = Build.A.BlockHeader.WithNumber(9).WithStateRoot(TestItem.KeccakA).TestObject;
        Assert.That(ctx.StateReader.HasStateForBlock(unknown), Is.False);
        Assert.Throws<MissingTrieNodeException>(() => ctx.StateReader.TryGetAccount(unknown, address, out _));
        Assert.That(ctx.StateReader.TryGetAccount(header4, TestItem.AddressB, out _), Is.False);

        // code reads come from the code db, with the empty-code shortcut
        ctx.CodeDb[TestItem.KeccakB.Bytes] = Bytes.FromHexString("0x6001");
        Assert.That(ctx.StateReader.GetCode(TestItem.KeccakB), Is.EqualTo(Bytes.FromHexString("0x6001")));
        Assert.That(ctx.StateReader.GetCode(Keccak.OfAnEmptyString), Is.Empty);
    }
}
