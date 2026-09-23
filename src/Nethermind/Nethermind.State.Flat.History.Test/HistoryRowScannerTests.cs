// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.Flat.History.Walk;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class HistoryRowScannerTests
{
    [Test]
    public void GenesisImport_WhenReopened_PreservesVerifiedState([Values] bool rocks)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory = new();
        using MemDb code = new();
        IDbFactory factory = new ScratchDbFactory(directory.Path, memory, rocks);
        Account account = new(3, 123);
        StateTree tree = new();
        tree.Set(TestItem.AddressA, account);
        tree.UpdateRootHash();
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(tree.RootHash).TestObject.Header;
        using (BulkFillSession session = new(factory, code, TestItem.KeccakA, anchor, false))
        {
            using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
            HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
            RecordScanRow(source.GetColumnDb(FlatHistoryColumns.AccountHistory), FlatHistoryColumns.AccountHistory,
                TestItem.AddressA.ToAccountPath.Bytes, 0, AccountDecoder.Slim.EncodeAsBytes(account));
            session.ImportPage(Store(source, FlatHistoryColumns.AccountHistory), format, FlatHistoryColumns.AccountHistory, CancellationToken.None);
            session.ImportGenesis([new(TestItem.AddressA, account)], CancellationToken.None);
            Assert.That(session.IsReady, Is.True);
        }
        using BulkFillSession reopened = new(factory, code, TestItem.KeccakA, anchor, false);
        Assert.That(reopened.IsReady, Is.True);
        Assert.That(reopened.CreateReader().GetAccount(TestItem.AddressA), Is.EqualTo(account));
        Assert.That(reopened.CurrentState.BlockNumber, Is.Zero);
    }

    [Test]
    public void GenesisImport_WhenWalSyncFails_RequiresReopenAndVerification()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using FailingWalScratchDb memory = new();
        using MemDb code = new();
        IDbFactory factory = new ScratchDbFactory(directory.Path, memory, false);
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        using (BulkFillSession session = new(factory, code, TestItem.KeccakA, anchor, false))
        {
            memory.IsWalFailureEnabled = true;
            Assert.Throws<IOException>(() => session.ImportGenesis([], CancellationToken.None));
            Assert.That(session.IsReady, Is.False);
            Assert.Throws<InvalidOperationException>(() => session.VerifyAnchor(CancellationToken.None));
        }
        Assert.Throws<IOException>(() => new BulkFillSession(factory, code, TestItem.KeccakA, anchor, false));
        memory.IsWalFailureEnabled = false;
        using BulkFillSession recovered = new(factory, code, TestItem.KeccakA, anchor, false);
        Assert.That(recovered.IsReady, Is.False);
        recovered.ImportGenesis([], CancellationToken.None);
        Assert.That(recovered.IsReady, Is.True);
    }

    [Test]
    public void GenesisImport_WhenRootMismatches_DoesNotPublishReadyCheckpoint()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory = new();
        using MemDb code = new();
        IDbFactory factory = new ScratchDbFactory(directory.Path, memory, false);
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        using (BulkFillSession session = new(factory, code, TestItem.KeccakA, anchor, false))
        {
            Assert.Throws<ScratchStateUnusableException>(() => session.ImportGenesis([new(TestItem.AddressA, new Account(0, 1))], CancellationToken.None));
            Assert.That(session.IsReady, Is.False);
        }
        using BulkFillSession reopened = new(factory, code, TestItem.KeccakA, anchor, false);
        Assert.That(reopened.IsReady, Is.False);
    }

    [Test]
    public void GenesisImport_WhenCancelledMidBatch_DoesNotCommitAccounts()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory = new();
        using MemDb code = new();
        using CancellationTokenSource cancellation = new();
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        using BulkFillSession session = new(new ScratchDbFactory(directory.Path, memory, false), code, TestItem.KeccakA, anchor, false);

        Assert.Throws<OperationCanceledException>(() => session.ImportGenesis(Allocations(), cancellation.Token));
        Assert.That(memory.GetColumnDb(BulkFillScratchState.Columns.Accounts).GetAllKeys(), Is.Empty);
        Assert.That(session.IsReady, Is.False);

        IEnumerable<KeyValuePair<Address, Account>> Allocations()
        {
            yield return new(TestItem.AddressA, new Account(0, 1));
            cancellation.Cancel();
            yield return new(TestItem.AddressB, new Account(0, 2));
        }
    }

    [Test]
    public void BulkReplay_WhenCheckpointSyncFails_RequiresReopenBeforeAdvancing()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using FailingWalScratchDb memory = new();
        using MemDb code = new();
        IDbFactory factory = new ScratchDbFactory(directory.Path, memory, false);
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        BlockHeader next = Build.A.Block.WithNumber(1).WithParentHash(anchor.Hash!).TestObject.Header;
        using (BulkFillSession session = new(factory, code, TestItem.KeccakA, anchor, false))
        {
            ImportEmptyState(session);
            session.BeginBlock(next);
            session.StageFinalState(() => new ScratchSnapshot(writer => writer.Set(TestItem.AddressA, new Account(1, 123))));
            memory.IsWalFailureEnabled = true;
            Assert.Throws<IOException>(session.CommitBlock);
            Assert.That(session.CurrentState.BlockNumber, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => session.BeginBlock(next));
        }
        Assert.Throws<IOException>(() => new BulkFillSession(factory, code, TestItem.KeccakA, anchor, false));
        memory.IsWalFailureEnabled = false;
        using BulkFillSession recovered = new(factory, code, TestItem.KeccakA, anchor, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.CurrentState.BlockNumber, Is.EqualTo(1UL));
            Assert.That(recovered.CreateReader().GetAccount(TestItem.AddressA)?.Balance, Is.EqualTo(new UInt256(123)));
        }
    }

    [Test]
    public void BulkReplay_WhenReopened_RetainsOnlyCommittedState([Values] bool rocks, [Values] bool commit)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory = new();
        using MemDb code = new();
        IDbFactory factory = new ScratchDbFactory(directory.Path, memory, rocks);
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        BlockHeader next = Build.A.Block.WithNumber(1).WithParentHash(anchor.Hash!).TestObject.Header;
        byte[] bytecode = [0x60, 0x01, 0x00];
        ValueHash256 codeHash = ValueKeccak.Compute(bytecode);
        using (BulkFillSession session = new(factory, code, TestItem.KeccakA, anchor, false))
        {
            ImportEmptyState(session);
            session.BeginBlock(next);
            using (IWorldStateScopeProvider.ICodeSetter writer = session.BeginCodeWrite()) writer.Set(codeHash, bytecode);
            session.StageFinalState(() => new ScratchSnapshot(writer =>
            {
                writer.Set(TestItem.AddressA, new Account(2, 123, Keccak.EmptyTreeHash, codeHash.ToCommitment()));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = writer.CreateStorageWriteBatch(TestItem.AddressA, 1);
                storage.Set(7, 99);
            }));
            Assert.That(session.CurrentState.BlockNumber, Is.Zero, "staging must not advance the durable checkpoint");
            if (commit) session.CommitBlock();
        }

        using BulkFillSession reopened = new(factory, code, TestItem.KeccakA, anchor, false);
        BulkFillStateReader reader = reopened.CreateReader();
        UInt256 slot = default;
        reader.TryGetSlot(TestItem.AddressA, 7, ref slot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopened.CurrentState.BlockNumber, Is.EqualTo(commit ? 1UL : 0UL));
            Assert.That(reader.GetAccount(TestItem.AddressA)?.Balance, Is.EqualTo(commit ? new UInt256(123) : (UInt256?)null));
            Assert.That(slot, Is.EqualTo(commit ? new UInt256(99) : UInt256.Zero));
            Assert.That(code.GetAllKeys(), Is.Empty, "scratch code must not leak into the live code database");
            if (commit) Assert.That(reopened.GetCode(codeHash), Is.EqualTo(bytecode));
            else Assert.Throws<InvalidDataException>(() => reopened.GetCode(codeHash));
        }
    }

    [Test]
    public void BulkReplay_WhenStorageIsCleared_ReclaimsOldSlotsAndPreservesNewOnes([Values] bool deleted, [Values] bool rlpWrapped)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory = new();
        using MemDb code = new();
        IDbFactory factory = new ScratchDbFactory(directory.Path, memory, false);
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        using BulkFillSession session = new(factory, code, TestItem.KeccakA, anchor, rlpWrapped);
        ImportEmptyState(session);
        BlockHeader first = Build.A.Block.WithNumber(1).WithParentHash(anchor.Hash!).TestObject.Header;
        CommitScratch(session, first, writer =>
        {
            writer.Set(TestItem.AddressA, new Account(1, 100));
            using IWorldStateScopeProvider.IStorageWriteBatch storage = writer.CreateStorageWriteBatch(TestItem.AddressA, 1025);
            for (uint slot = 0; slot < 1025; slot++) storage.Set(slot, 1);
        });
        BlockHeader second = Build.A.Block.WithNumber(2).WithParentHash(first.Hash!).TestObject.Header;
        CommitScratch(session, second, writer =>
        {
            writer.Set(TestItem.AddressA, deleted ? null : new Account(1, 100));
            using IWorldStateScopeProvider.IStorageWriteBatch storage = writer.CreateStorageWriteBatch(TestItem.AddressA, 1);
            storage.Clear();
            storage.Set(2000, 2);
        });
        Assert.Throws<OperationCanceledException>(() => session.CleanStorage(new CancellationToken(true)));
        Assert.That(memory.GetColumnDb(BulkFillScratchState.Columns.Clears).GetAllKeys(), Is.Not.Empty);

        session.CleanStorage(CancellationToken.None);
        session.CleanStorage(CancellationToken.None);

        BulkFillStateReader reader = session.CreateReader();
        UInt256 oldValue = default;
        UInt256 newValue = default;
        reader.TryGetSlot(TestItem.AddressA, 0, ref oldValue);
        reader.TryGetSlot(TestItem.AddressA, 2000, ref newValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(oldValue, Is.EqualTo(UInt256.Zero));
            Assert.That(newValue, Is.EqualTo(deleted ? UInt256.Zero : new UInt256(2)));
            Assert.That(memory.GetColumnDb(BulkFillScratchState.Columns.Storage).GetAllKeys().Count(), Is.EqualTo(deleted ? 0 : 1));
            Assert.That(memory.GetColumnDb(BulkFillScratchState.Columns.Clears).GetAllKeys(), Is.Empty);
        }
    }

    [Test]
    public void BulkReplay_WhenReleased_CanBootstrapAgainWithoutOldRows([Values] bool rocks)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory = new();
        using MemDb code = new();
        IDbFactory factory = new ScratchDbFactory(directory.Path, memory, rocks);
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        using (BulkFillSession session = new(factory, code, TestItem.KeccakA, anchor, false))
        {
            ImportEmptyState(session);
            BlockHeader next = Build.A.Block.WithNumber(1).WithParentHash(anchor.Hash!).TestObject.Header;
            CommitScratch(session, next, writer => writer.Set(TestItem.AddressA, new Account(1, 100)));
            session.ReleaseState(CancellationToken.None);
            Assert.That(session.IsReady, Is.False);
        }

        using BulkFillSession reopened = new(factory, code, TestItem.KeccakA, anchor, false);
        Assert.That(reopened.IsReady, Is.False);
        ImportEmptyState(reopened);
        Assert.That(reopened.CreateReader().GetAccount(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void BulkReplay_WhenReleaseIsCancelled_LeavesNoReplayBaseAndFinishesOnTheNextRelease()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory = new();
        using MemDb code = new();
        IDbFactory factory = new ScratchDbFactory(directory.Path, memory, false);
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        using (BulkFillSession session = new(factory, code, TestItem.KeccakA, anchor, false))
        {
            ImportEmptyState(session);
            BlockHeader next = Build.A.Block.WithNumber(1).WithParentHash(anchor.Hash!).TestObject.Header;
            CommitScratch(session, next, writer => writer.Set(TestItem.AddressA, new Account(1, 100)));

            Assert.Throws<OperationCanceledException>(() => session.ReleaseState(new CancellationToken(true)));
            Assert.That(session.IsReady, Is.False, "the checkpoint goes before the rows, so a cut-short release is never mistaken for a replay base");
        }

        using BulkFillSession reopened = new(factory, code, TestItem.KeccakA, anchor, false);
        Assert.That(reopened.CreateReader().GetAccount(TestItem.AddressA), Is.Not.Null, "precondition: the rows outlive the cancelled release");
        reopened.ReleaseState(CancellationToken.None);
        Assert.That(reopened.CreateReader().GetAccount(TestItem.AddressA), Is.Null, "the next release finishes what the cancelled one started");
    }

    [Test]
    public void BulkReplay_WhenABlockReadsAnAccountThatDoesNotExist_WritesNoRemovalForIt()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory = new();
        using MemDb code = new();
        BlockHeader anchor = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject.Header;
        using BulkFillSession session = new(new ScratchDbFactory(directory.Path, memory, false), code, TestItem.KeccakA, anchor, false);
        ImportEmptyState(session);
        BlockHeader first = Build.A.Block.WithNumber(1).WithParentHash(anchor.Hash!).TestObject.Header;
        CommitScratch(session, first, writer => writer.Set(TestItem.AddressA, new Account(1, 100)));
        BlockHeader second = Build.A.Block.WithNumber(2).WithParentHash(first.Hash!).TestObject.Header;

        // The block snapshot carries every account the block touched: A, which it removes, and B, which it only read
        // and which does not exist, the shape of a block probing thousands of empty addresses.
        CommitScratch(session, second, writer =>
        {
            writer.Set(TestItem.AddressA, null);
            writer.Set(TestItem.AddressB, null);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(memory.GetColumnDb(BulkFillScratchState.Columns.Clears).GetAllKeys(), Is.EqualTo(new[] { TestItem.AddressA.ToAccountPath.Bytes.ToArray() }),
                "only the account the scratch held is a removal; a read of a missing account is not, and the cleanup must not be handed a clear for it");
            Assert.That(session.CreateReader().GetAccount(TestItem.AddressA), Is.Null);
        }
    }

    private static void ImportEmptyState(BulkFillSession session)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
        foreach (FlatHistoryColumns column in new[] { FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears })
            Assert.That(session.ImportPage(Store(source, column), format, column, CancellationToken.None), Is.True);
        session.VerifyAnchor(CancellationToken.None);
    }

    private static void CommitScratch(BulkFillSession session, BlockHeader header, Action<IWorldStateScopeProvider.IWorldStateWriteBatch> write)
    {
        session.BeginBlock(header);
        session.StageFinalState(() => new ScratchSnapshot(write));
        session.CommitBlock();
    }

    private sealed class ScratchSnapshot(Action<IWorldStateScopeProvider.IWorldStateWriteBatch> write) : IWorldStateScopeProvider.IBlockChangeSnapshot
    {
        public void WriteTo(IWorldStateScopeProvider.IWorldStateWriteBatch batch) => write(batch);
        public void Dispose() { }
    }

    [Test]
    public void ScratchVerification_WhenStorageWasCleared_ChecksTheSurvivingState(
        [Values] bool rlpWrapped,
        [Values(4UL, 5UL, 6UL)] ulong clearAt,
        [Values] ScratchCorruption corruption)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> scratch = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
        ValueHash256 address = Keccak.Compute("scratch account").ValueHash256;
        ValueHash256 slot = Keccak.Compute("scratch slot").ValueHash256;
        using MemDb storageDb = new();
        StorageTree storageTree = new(new RawScopedTrieStore(storageDb), LimboLogs.Instance);
        if (clearAt <= 5) storageTree.Set(slot.Bytes, new byte[] { 0x81, 0x80 });
        storageTree.UpdateRootHash();
        Account account = new(1, 2, storageTree.RootHash, Keccak.OfAnEmptyString);
        byte[] accountRow = AccountDecoder.Slim.EncodeAsBytes(account);
        RecordScanRow(source.GetColumnDb(FlatHistoryColumns.AccountHistory), FlatHistoryColumns.AccountHistory, address.Bytes, 5, accountRow);
        RecordScanRow(source.GetColumnDb(FlatHistoryColumns.StorageClears), FlatHistoryColumns.StorageClears, address.Bytes, clearAt, []);
        Span<byte> storageKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(storageKey, address, slot);
        Span<byte> encoded = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int length = BaseFlatPersistence.EncodeSlotValue(new UInt256(128), rlpWrapped, encoded);
        RecordScanRow(source.GetColumnDb(FlatHistoryColumns.StorageHistory), FlatHistoryColumns.StorageHistory, storageKey, 5, encoded[..length]);
        using MemDb accountsDb = new();
        StateTree accountsTree = new(new RawScopedTrieStore(accountsDb), LimboLogs.Instance);
        AccountRowRlp.Set(accountsTree, address, accountRow);
        accountsTree.UpdateRootHash();
        BulkFillScratchState state = new(scratch, Keccak.EmptyTreeHash, 8);
        foreach (FlatHistoryColumns column in new[] { FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears })
            state.ImportPage(Store(source, column), format, column, 10, CancellationToken.None);
        // A slot row is only decoded as RLP in the wrapped format, and only when it survives the clear: a slot older
        // than the clear is dead and skipped before decoding. In the other cases the malformed row has nothing to
        // corrupt and the run is left as a passing one.
        bool slotCorruptionApplies = corruption == ScratchCorruption.MalformedSlotRow && rlpWrapped && clearAt <= 5;
        switch (corruption)
        {
            case ScratchCorruption.WrongBalance:
                scratch.GetColumnDb(BulkFillScratchState.Columns.Accounts).PutSpan(address.Bytes, AccountDecoder.Slim.EncodeAsBytes(account.WithChangedBalance(99)));
                break;
            case ScratchCorruption.MalformedAccountRow:
                scratch.GetColumnDb(BulkFillScratchState.Columns.Accounts).PutSpan(address.Bytes, [0xff]);
                break;
            case ScratchCorruption.MalformedSlotRow when slotCorruptionApplies:
                // The scratch orders slots under a key of its own, not the history row key, so the corruption goes
                // where the import put the row; under any other key the verifier never sees it.
                byte[] scratchSlotKey = scratch.GetColumnDb(BulkFillScratchState.Columns.Storage).GetAllKeys().Single();
                Span<byte> malformed = stackalloc byte[sizeof(ulong) + 1];
                BinaryPrimitives.WriteUInt64BigEndian(malformed, 5);
                malformed[sizeof(ulong)] = 0xff;
                scratch.GetColumnDb(BulkFillScratchState.Columns.Storage).PutSpan(scratchSlotKey, malformed);
                break;
        }

        bool expectsFailure = corruption == ScratchCorruption.WrongBalance
            || corruption == ScratchCorruption.MalformedAccountRow
            || slotCorruptionApplies;
        if (expectsFailure)
            Assert.Throws<ScratchStateUnusableException>(() => state.VerifyAnchor(accountsTree.RootHash, rlpWrapped, CancellationToken.None),
                "a base that fails verification stays wrong on every retry, so it must carry the type the replay stops on rather than one it retries");
        else
            Assert.DoesNotThrow(() => state.VerifyAnchor(accountsTree.RootHash, rlpWrapped, CancellationToken.None));
    }

    public enum ScratchCorruption
    {
        None,
        WrongBalance,
        MalformedAccountRow,
        MalformedSlotRow
    }

    [Test]
    public void SortedStateRoot_WhenKeysSharePrefixes_MatchesPatriciaTree(
        [Values(0, 1, 2, 17, 1024)] int count,
        [Values(0, 16, 30)] int sharedBytes)
    {
        ValueHash256[] keys = new ValueHash256[count];
        for (int index = 0; index < count; index++)
        {
            byte[] bytes = new byte[Hash256.Size];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(Hash256.Size - sizeof(int)), index);
            keys[index] = ValueKeccak.Compute(bytes);
            keys[index].BytesAsSpan[..sharedBytes].Clear();
            if (sharedBytes == 30) BinaryPrimitives.WriteUInt16BigEndian(keys[index].BytesAsSpan[30..], (ushort)index);
        }
        Array.Sort(keys, static (first, second) => first.Bytes.SequenceCompareTo(second.Bytes));
        using MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        using SortedStateRoot streamed = new();
        for (int index = 0; index < keys.Length; index++)
        {
            byte[] value = [(byte)(1 + index % 127)];
            tree.Set(keys[index].Bytes, value);
            streamed.Add(keys[index], value);
        }
        tree.UpdateRootHash();

        Assert.That(streamed.Finish(), Is.EqualTo(tree.RootHash.ValueHash256), "bounded streaming must produce the same root as the existing trie implementation");
    }

    [Test]
    public void SortedStateRoot_WhenInputIsNotStrictlyIncreasing_RefusesIt([Values] bool duplicate)
    {
        using SortedStateRoot streamed = new();
        ValueHash256 key = new(ScanKey(Hash256.Size, 1));
        streamed.Add(key, [1]);

        Assert.Throws<InvalidDataException>(() => streamed.Add(duplicate ? key : default, [2]));
    }

    [Test]
    public void ScratchImport_WhenWalSyncFails_RequiresDurableRecoveryBeforeReportingCompletion()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
        using FailingWalScratchDb scratch = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
        BulkFillScratchState state = new(scratch, Keccak.EmptyTreeHash, 0);
        scratch.IsWalFailureEnabled = true;

        Assert.Throws<IOException>(() => state.ImportPage(Store(source, FlatHistoryColumns.AccountHistory), format,
            FlatHistoryColumns.AccountHistory, 1, CancellationToken.None));
        Assert.Throws<IOException>(() => new BulkFillScratchState(scratch, Keccak.EmptyTreeHash, 0),
            "a visible completed checkpoint must not bypass the failed durability barrier on reopen");
        scratch.IsWalFailureEnabled = false;
        int syncs = scratch.SuccessfulSyncs;
        BulkFillScratchState recovered = new(scratch, Keccak.EmptyTreeHash, 0);
        HistoricalStateScan.Page page = recovered.ImportPage(Store(source, FlatHistoryColumns.AccountHistory), format,
            FlatHistoryColumns.AccountHistory, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Complete, Is.True);
            Assert.That(scratch.SuccessfulSyncs, Is.GreaterThan(syncs), "successful recovery must establish durability before accepting completion");
        }
    }

    [Test]
    public void ScratchImport_WhenReopened_ResumesCommittedPages(
        [Values(FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears)] FlatHistoryColumns column)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> scratch = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
        int keyLength = column == FlatHistoryColumns.StorageHistory ? BaseFlatPersistence.StorageKeyLength : Hash256.Size;
        byte[] key = ScanKey(keyLength, 1);
        RecordScanRow(source.GetColumnDb(column), column, key, 5, column == FlatHistoryColumns.StorageClears ? [] : [5]);
        RecordScanRow(source.GetColumnDb(column), column, key, 10, column == FlatHistoryColumns.StorageClears ? [] : [10]);
        BulkFillScratchState state = new(scratch, Keccak.EmptyTreeHash, 10);
        HistoricalStateScan.Page first = state.ImportPage(Store(source, column), format, column, 1, CancellationToken.None);
        Assert.That(first.Complete, Is.False, "the first page must leave a resumable checkpoint");

        state = new BulkFillScratchState(scratch, Keccak.EmptyTreeHash, 10);
        HistoricalStateScan.Page last = state.ImportPage(Store(source, column), format, column, 2, CancellationToken.None);
        BulkFillScratchState.Columns target = column switch
        {
            FlatHistoryColumns.AccountHistory => BulkFillScratchState.Columns.Accounts,
            FlatHistoryColumns.StorageHistory => BulkFillScratchState.Columns.Storage,
            _ => BulkFillScratchState.Columns.Clears,
        };
        byte[] expected = column == FlatHistoryColumns.AccountHistory ? [10] : new byte[sizeof(ulong) + (column == FlatHistoryColumns.StorageHistory ? 1 : 0)];
        if (column != FlatHistoryColumns.AccountHistory) BinaryPrimitives.WriteUInt64BigEndian(expected, 10);
        if (column == FlatHistoryColumns.StorageHistory) expected[^1] = 10;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.Complete, Is.True);
            Assert.That(last.Scanned, Is.EqualTo(1), "restart must not reread the committed row");
            Assert.That(scratch.GetColumnDb(target)[key], Is.EqualTo(expected), "resuming within a key must retain the selected version");
            Assert.That(state.ImportPage(Store(source, column), format, column, 1, CancellationToken.None).Scanned, Is.Zero, "completed imports must not restart");
        }
    }

    [Test]
    public void ScratchImport_WhenScanFails_DiscardsRowsAndCheckpoint()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> scratch = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
        IDb history = source.GetColumnDb(FlatHistoryColumns.AccountHistory);
        byte[] key = ScanKey(Hash256.Size, 1);
        RecordScanRow(history, FlatHistoryColumns.AccountHistory, key, 0, [1]);
        byte[] malformed = ScanKey(Hash256.Size, 2);
        history.PutSpan(malformed, [2]);
        BulkFillScratchState state = new(scratch, Keccak.EmptyTreeHash, 0);

        Assert.Throws<InvalidDataException>(() => state.ImportPage((ISortedKeyValueStore)history, format, FlatHistoryColumns.AccountHistory, 2, CancellationToken.None));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scratch.GetColumnDb(BulkFillScratchState.Columns.Accounts)[key], Is.Null, "a callback before the error must not leak a partial page");
            Assert.That(scratch.GetColumnDb(BulkFillScratchState.Columns.Metadata)[new byte[] { (byte)BulkFillScratchState.Columns.Accounts }], Is.Null);
        }
        Assert.Throws<InvalidOperationException>(() => state.ImportPage((ISortedKeyValueStore)history, format, FlatHistoryColumns.AccountHistory, 1, CancellationToken.None));
        BulkFillScratchState reopened = new(scratch, Keccak.EmptyTreeHash, 0);
        Assert.That(reopened.ImportPage((ISortedKeyValueStore)history, format, FlatHistoryColumns.AccountHistory, 1, CancellationToken.None).Scanned, Is.EqualTo(1));
    }

    [Test]
    public void ScratchImport_WhenIdentityChanges_RefusesResume([Values] bool changeAnchor)
    {
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> scratch = new();
        _ = new BulkFillScratchState(scratch, Keccak.EmptyTreeHash, 5);

        Assert.Throws<ScratchStateUnusableException>(() => new BulkFillScratchState(scratch,
            changeAnchor ? Keccak.EmptyTreeHash : Keccak.Zero, changeAnchor ? 6UL : 5UL));
    }

    [Test]
    public void ScratchImport_WhenReplayVersionIsObsolete_RefusesResumeWithoutChangingRows()
    {
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> scratch = new();
        _ = new BulkFillScratchState(scratch, Keccak.EmptyTreeHash, 5);
        IDb metadata = scratch.GetColumnDb(BulkFillScratchState.Columns.Metadata);
        byte[] manifest = metadata["bulk-fill-identity"u8]!;
        manifest[0] = 2;
        metadata.PutSpan("bulk-fill-identity"u8, manifest);
        IDb accounts = scratch.GetColumnDb(BulkFillScratchState.Columns.Accounts);
        accounts.PutSpan(TestItem.KeccakA.Bytes, [1, 2, 3]);

        Assert.Throws<NotSupportedException>(() => new BulkFillScratchState(scratch, Keccak.EmptyTreeHash, 5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata["bulk-fill-identity"u8], Is.EqualTo(manifest));
            Assert.That(accounts[TestItem.KeccakA.Bytes], Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }

    [Test]
    public void ReadPage_WhenNoVersionExistsAtAnchor_EmitsNothing(
        [Values(FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears)] FlatHistoryColumns column,
        [Values] bool empty)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        IDb source = columns.GetColumnDb(column);
        int keyLength = column == FlatHistoryColumns.StorageHistory ? BaseFlatPersistence.StorageKeyLength : Hash256.Size;
        if (!empty) RecordScanRow(source, column, ScanKey(keyLength, 0xFF), 1, []);
        HistoricalStateScan scanner = new((ISortedKeyValueStore)source, format, column, 0);
        int callbacks = 0;

        HistoricalStateScan.Page page = scanner.ReadPage(null, 2, (_, _, _) => callbacks++, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callbacks, Is.Zero);
            Assert.That(page.Scanned, Is.EqualTo(empty ? 0 : 1));
            Assert.That(page.Complete, Is.True);
        }
    }

    [Test]
    public void ReadPage_WhenResumed_SelectsTheSameVersionsAsPointReads(
        [Values(FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears)] FlatHistoryColumns column,
        [Values(1, 2, 5, 32)] int pageSize,
        [Values(0UL, 7UL, 15UL, 30UL)] ulong anchor)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        IDb source = columns.GetColumnDb(column);
        int keyLength = column == FlatHistoryColumns.StorageHistory ? BaseFlatPersistence.StorageKeyLength : Hash256.Size;
        byte[][] keys = [ScanKey(keyLength, 0), ScanKey(keyLength, 0x22), ScanKey(keyLength, 0xFF)];
        foreach (byte[] key in keys)
        {
            foreach (ulong block in new ulong[] { 0, 5, 10, 20 })
            {
                RecordScanRow(source, column, key, block, column == FlatHistoryColumns.StorageClears || block == 10 ? [] : [(byte)(block + 1)]);
            }
        }

        Dictionary<string, (ulong Block, byte[] Value)> selected = [];
        HistoricalStateScan.Cursor? cursor = null;
        int scanned = 0;
        int callbacks = 0;
        while (true)
        {
            HistoricalStateScan scanner = new((ISortedKeyValueStore)source, format, column, anchor);
            HistoricalStateScan.Page page = scanner.ReadPage(cursor, pageSize, (key, block, value) =>
            {
                callbacks++;
                selected[Convert.ToHexString(key)] = (block, value.ToArray());
            }, CancellationToken.None);
            Assert.That(page.Scanned, Is.LessThanOrEqualTo(pageSize), "a hot key must not overrun the raw-row page budget");
            scanned += page.Scanned;
            cursor = page.Position;
            if (page.Complete) break;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scanned, Is.EqualTo(12), "each source version is scanned exactly once across restarts");
            Assert.That(selected.Count, Is.EqualTo(keys.Length), "including tombstones and the all-FF identity");
            if (column != FlatHistoryColumns.StorageClears)
                Assert.That(callbacks, Is.EqualTo(keys.Length), "older versions must not overwrite the selected version after a page boundary");
        }

        foreach (byte[] key in keys)
        {
            (ulong block, byte[] value) = selected[Convert.ToHexString(key)];
            ulong expectedBlock = anchor >= 20 ? 20UL : anchor >= 10 ? 10UL : anchor >= 5 ? 5UL : 0UL;
            Assert.That(block, Is.EqualTo(expectedBlock), "a scan must select the last change at or below the anchor");
            if (column == FlatHistoryColumns.StorageClears) continue;

            HistoryStore pointReader = new(source, LimboLogs.Instance.GetClassLogger<HistoryStore>());
            byte[] expected = new byte[256];
            int length = pointReader.TryGetAt(anchor, key, expected, out ulong writtenAt);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(block, Is.EqualTo(writtenAt));
                Assert.That(value, Is.EqualTo(expected.AsSpan(0, length).ToArray()), "streaming selection must match the existing point lookup byte-for-byte");
            }
        }
    }

    [Test]
    public void ReadPage_WhenCancelledOrCallbackThrows_LeavesTheInputCursorReusable([Values] bool cancel)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        IDb source = columns.GetColumnDb(FlatHistoryColumns.AccountHistory);
        RecordScanRow(source, FlatHistoryColumns.AccountHistory, ScanKey(Hash256.Size, 1), 0, [1]);
        RecordScanRow(source, FlatHistoryColumns.AccountHistory, ScanKey(Hash256.Size, 2), 0, [2]);
        HistoricalStateScan scanner = new((ISortedKeyValueStore)source, format, FlatHistoryColumns.AccountHistory, 0);
        HistoricalStateScan.Cursor cursor = scanner.ReadPage(null, 1, (_, _, _) => { }, CancellationToken.None).Position!;
        byte[] original = cursor.Key.ToArray();
        using CancellationTokenSource cancellation = new();
        HistoricalStateScan.RowHandler fail = (_, _, _) =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("staging failed");
        };

        if (cancel)
            Assert.Throws<OperationCanceledException>(() => scanner.ReadPage(cursor, 1, fail, cancellation.Token));
        else
            Assert.Throws<IOException>(() => scanner.ReadPage(cursor, 1, fail, cancellation.Token));

        Assert.That(cursor.Key.ToArray(), Is.EqualTo(original), "failure must not mutate the caller's durable checkpoint");
        int replayed = 0;
        HistoricalStateScan.Page retry = scanner.ReadPage(cursor, 2, (_, _, value) =>
        {
            Assert.That(value.ToArray(), Is.EqualTo(new byte[] { 2 }), "retry must not skip the staged but uncommitted row");
            replayed++;
        }, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(1));
            Assert.That(retry.Complete, Is.True);
        }
    }

    [Test]
    public void ReadPage_WhenCursorHasDifferentIdentity_RefusesIt([Values] bool changeAnchor)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        HistoricalStateScan scanner = new(Store(columns, FlatHistoryColumns.AccountHistory), format, FlatHistoryColumns.AccountHistory, 10);
        HistoricalStateScan.Cursor cursor = new(changeAnchor ? FlatHistoryColumns.AccountHistory : FlatHistoryColumns.StorageClears,
            changeAnchor ? 11UL : 10UL, new byte[Hash256.Size + sizeof(ulong)]);

        Assert.Throws<ArgumentException>(() => scanner.ReadPage(cursor, 1, (_, _, _) => { }, CancellationToken.None));
    }

    [Test]
    public void ReadPage_WhenRowHasInvalidShape_RefusesIt([Values] bool invalidKey)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        IDb source = columns.GetColumnDb(FlatHistoryColumns.AccountHistory);
        source.PutSpan(new byte[Hash256.Size + sizeof(ulong) - (invalidKey ? 1 : 0)], invalidKey ? [1] : new byte[257]);
        HistoricalStateScan scanner = new((ISortedKeyValueStore)source, format, FlatHistoryColumns.AccountHistory, ulong.MaxValue);

        Assert.Throws<InvalidDataException>(() => scanner.ReadPage(null, 1, (_, _, _) => { }, CancellationToken.None));
    }

    [Test]
    public void HistoricalStateScan_WhenHistoryIsWindowed_RefusesTheDifferentRowSemantics()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig { HistoryRetention = HistoryRetentionMode.Rolling }).RowFormat;

        Assert.Throws<NotSupportedException>(() => new HistoricalStateScan(Store(columns, FlatHistoryColumns.AccountHistory), format, FlatHistoryColumns.AccountHistory, 10));
    }

    [Test]
    public void Contracts_sharing_a_storage_prefix_and_a_slot_past_the_streamed_key_limit_stream_at_full_depth_instead_of_splitting()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        const int colliding = HistoryRowScanner.MaxStreamedKeys + 4;
        ValueHash256 first = Identity(0x01);
        ValueHash256 slot = Keccak.Compute("slot").ValueHash256;
        for (byte identity = 1; identity <= colliding; identity++) RecordStorage(columns, Identity(identity), slot, block: 1, [identity]);
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig { HistoryEnabled = true });
        HistoryRowScanner scanner = new(Store(columns, FlatHistoryColumns.AccountHistory), Store(columns, FlatHistoryColumns.StorageHistory), Store(columns, FlatHistoryColumns.StorageClears), rowFormat);
        using StoragePartitionRows rows = new();
        TreePath fullDepth = new(slot, CommitmentDepthPolicy.MaxTrieDepth);
        byte[] prefix = first.Bytes[..HistoryRowScanner.StoragePrefixLength].ToArray();

        ScanOutcome outcome = scanner.ScanStorage(prefix, fullDepth, from: 0, to: 1, maxRows: 1, rows, [], CancellationToken.None);
        int streamed = 0;
        while (outcome == ScanOutcome.SinglePathOverflow)
        {
            streamed++;
            outcome = scanner.ScanStorage(prefix, fullDepth, from: 0, to: 1, maxRows: 1, rows, [], CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome, Is.EqualTo(ScanOutcome.Fits),
                "a 64-nibble slot prefix has no children to split into, so a partition that still does not fit streams its keys one identity at a time instead of asking for a deeper split");
            Assert.That(streamed, Is.EqualTo(colliding - 1), "at full depth the streamed-key cap does not apply: every colliding identity but the one that fits is streamed, because the alternative is a split that cannot exist");
            Assert.That(rows.Count, Is.EqualTo(1));
        }
    }

    private sealed class ScratchDbFactory(string directory, SnapshotableMemColumnsDb<BulkFillScratchState.Columns> memory, bool rocks) : IDbFactory
    {
        public string GetFullDbPath(DbSettings dbSettings) => directory;

        public IDb CreateDb(DbSettings dbSettings) => throw new NotSupportedException();

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
        {
            if (rocks)
                return new ColumnsDb<T>(directory, new DbSettings("TransactionIndexScratch", directory), new DbConfig(),
                    new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
                    LimboLogs.Instance, Enum.GetValues<T>());
            return (IColumnsDb<T>)(object)memory;
        }
    }

    private static ValueHash256 Identity(byte tail)
    {
        byte[] bytes = new byte[Hash256.Size];
        bytes[0] = 0x11;
        bytes[1] = 0x22;
        bytes[2] = 0x33;
        bytes[3] = 0x44;
        bytes[4] = tail;
        return new ValueHash256(bytes);
    }

    private static byte[] ScanKey(int length, byte value)
    {
        byte[] key = new byte[length];
        key.AsSpan().Fill(value);
        return key;
    }

    private static void RecordScanRow(IDb source, FlatHistoryColumns column, ReadOnlySpan<byte> key, ulong block, ReadOnlySpan<byte> value)
    {
        byte[] rowKey = new byte[key.Length + sizeof(ulong)];
        key.CopyTo(rowKey);
        BinaryPrimitives.WriteUInt64BigEndian(rowKey.AsSpan(key.Length), column == FlatHistoryColumns.StorageClears ? block : ~block);
        if (value.IsEmpty) source.Set(rowKey, []);
        else source.PutSpan(rowKey, value);
    }

    private static ISortedKeyValueStore Store(IColumnsDb<FlatHistoryColumns> columns, FlatHistoryColumns column) => (ISortedKeyValueStore)columns.GetColumnDb(column);

    private static void RecordStorage(IColumnsDb<FlatHistoryColumns> columns, in ValueHash256 identity, in ValueHash256 slot, ulong block, ReadOnlySpan<byte> rawValue)
    {
        HistoryStore store = new(columns.GetColumnDb(FlatHistoryColumns.StorageHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[BaseFlatPersistence.StorageKeyLength], identity, slot);
        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = BaseFlatPersistence.EncodeSlotValue(BaseFlatPersistence.DecodeSlotValue(rawValue), rlpWrapSlots: true, value);
        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        store.RecordChange(block, flatKey, value[..written], batch.GetColumnBatch(FlatHistoryColumns.StorageHistory));
    }

    private sealed class FailingWalScratchDb : SnapshotableMemColumnsDb<BulkFillScratchState.Columns>, IColumnsDb<BulkFillScratchState.Columns>
    {
        public bool IsWalFailureEnabled { get; set; }
        public int SuccessfulSyncs { get; private set; }

        public void SyncWal()
        {
            if (IsWalFailureEnabled) throw new IOException("WAL sync failed");
            SuccessfulSyncs++;
        }
    }
}
