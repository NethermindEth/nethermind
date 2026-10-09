// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FastEnumUtility;
using Autofac;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;
using NUnit.Framework;
using static Nethermind.State.Pbt.Test.PbtStoreTestExtensions;

namespace Nethermind.State.Pbt.Test;

public class PbtRocksDbPersistenceTests
{
    private static ReadOnlySpan<byte> CurrentStateKey => "currentState"u8;
    private static ReadOnlySpan<byte> SchemaEpochKey => "schemaEpoch"u8;
    private static ReadOnlySpan<byte> ValidStateKey => "validState"u8;
    private static ReadOnlySpan<byte> NodeGroupKeyLayoutKey => "nodeGroupKeyLayout"u8;
    private static ReadOnlySpan<byte> PrefixlessBranchOmissionKey => "prefixlessBranchOmission"u8;

    [Test]
    public void Retired_schema_stamps_are_accepted_and_count_as_schema_stamps()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        metadata[SchemaEpochKey] = Epoch(23);
        metadata[NodeGroupKeyLayoutKey] = [1];
        metadata[PrefixlessBranchOmissionKey] = [1];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig(), NullTrieNodeLog.Instance), Throws.Nothing);
            Assert.That(PbtRocksDbPersistence.IsSchemaStamp(NodeGroupKeyLayoutKey) && PbtRocksDbPersistence.IsSchemaStamp(PrefixlessBranchOmissionKey), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Old_epoch_rejection_does_not_mutate_any_column(bool importEnabled)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        foreach (PbtColumns column in FastEnum.GetValues<PbtColumns>())
            db.GetColumnDb(column)[new byte[] { 1 }] = [2];
        db.GetColumnDb(PbtColumns.Metadata)[SchemaEpochKey] = Epoch(15);
        Dictionary<PbtColumns, KeyValuePair<byte[], byte[]>[]> before = [];
        foreach (PbtColumns column in FastEnum.GetValues<PbtColumns>())
            before[column] = db.GetColumnDb(column).GetAll(ordered: true).ToArray();

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = importEnabled }, NullTrieNodeLog.Instance),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("re-import"));

        using (Assert.EnterMultipleScope())
        {
            foreach (PbtColumns column in FastEnum.GetValues<PbtColumns>())
            {
                KeyValuePair<byte[], byte[]>[] after = db.GetColumnDb(column).GetAll(ordered: true).ToArray();
                Assert.That(after.Select(entry => entry.Key), Is.EqualTo(before[column].Select(entry => entry.Key)), $"{column} keys");
                Assert.That(after.Select(entry => entry.Value), Is.EqualTo(before[column].Select(entry => entry.Value)), $"{column} values");
            }
        }
    }

    [Test]
    public void Slots_persist_as_whole_runs_that_are_replaced_and_deleted_wholesale()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        EvmWord value = EvmWordSlot.FromStripped(Bytes.FromHexString("0x1234"));
        static PbtPath Key(uint slot) => PbtStateKey.HeaderStorage(PbtStateKey.AddressKeyHash(TestItem.AddressA), slot);
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        StateId second = new(2, TestItem.KeccakB.ValueHash256);
        StateId third = new(3, TestItem.KeccakC.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            foreach (uint start in new[] { 0u, 16u })
            {
                PackedSlotRun run = SlotRun.Empty;
                for (uint slot = start; slot <= Math.Min(start + 15, 20); slot++)
                {
                    PackedSlotRun previous = run;
                    run = run.With(SlotRun.IndexOf(Key(slot)), value);
                    SlotRun.Return(previous);
                }
                batch.SetSlotRun(SlotRun.RunKey(Key(start)), run);
                SlotRun.Return(run);
            }
            batch.Commit();
        }
        byte[][] populatedRows = db.GetColumnDb(PbtColumns.Storages).GetAllKeys().ToArray();
        using IPbtPersistence.IReader populated = persistence.CreateReader();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(first, second, default, WriteFlags.None))
        {
            batch.SetSlot(Key(3), value);
            batch.Commit();
        }
        using IPbtPersistence.IReader replaced = persistence.CreateReader();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(second, third, default, WriteFlags.None))
        {
            batch.SetSlotRun(SlotRun.RunKey(Key(3)), SlotRun.Empty);
            batch.Commit();
        }
        using IPbtPersistence.IReader deleted = persistence.CreateReader();
        PackedSlotRun tail = populated.GetSlotRun(SlotRun.RunKey(Key(16)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(populatedRows, Is.EquivalentTo(new[] { Persisted(SlotRun.RunKey(Key(0))), Persisted(SlotRun.RunKey(Key(16))) }));
            Assert.That(Enumerable.Range(0, 22).Select(slot => populated.GetSlot(Key((uint)slot))), Is.EqualTo(Enumerable.Range(0, 22).Select(slot => slot <= 20 ? value : default)));
            Assert.That(tail.Count, Is.EqualTo(5));
            Assert.That(populated.GetSlotRun(SlotRun.RunKey(Key(32))), Is.SameAs(SlotRun.Empty));
            Assert.That(new[] { replaced.GetSlot(Key(0)), replaced.GetSlot(Key(3)), replaced.GetSlot(Key(16)) }, Is.EqualTo(new[] { default, value, value }));
            Assert.That(Enumerable.Range(0, 22).Count(slot => !EvmWordSlot.IsZero(replaced.GetSlot(Key((uint)slot)))), Is.EqualTo(6), "the rewritten run drops its other slots");
            Assert.That(deleted.GetSlot(Key(3)), Is.EqualTo(default(EvmWord)));
            Assert.That(db.GetColumnDb(PbtColumns.Storages).GetAllKeys(), Is.EquivalentTo(new[] { Persisted(SlotRun.RunKey(Key(16))) }));
        }
        SlotRun.Return(tail);
    }

    [Test]
    public void Storage_rows_are_keyed_address_first_so_one_account_is_one_range([Values(1u, 64u)] uint otherAddressSlot)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        PbtVariableTreeKey headerKey = PbtTestLeaves.SlotKey(TestItem.AddressA, 1);
        PbtVariableTreeKey overflowKey = PbtTestLeaves.SlotKey(TestItem.AddressA, PbtKeyDerivation.HeaderStorageOffset);
        PbtVariableTreeKey otherAddressKey = PbtTestLeaves.SlotKey(TestItem.AddressB, otherAddressSlot);
        EvmWord value = EvmWordSlot.FromStripped(Bytes.FromHexString("0x1234"));
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            batch.SetSlot(headerKey, value);
            batch.SetSlot(overflowKey, value);
            batch.SetSlot(otherAddressKey, value);
            batch.Commit();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Persisted(headerKey), Is.EqualTo(Bytes.Concat(addressHash.Bytes, [Eip8297KeyDerivation.AccountZone], headerKey.Bytes[33..])));
            Assert.That(Persisted(overflowKey), Is.EqualTo(Bytes.Concat(addressHash.Bytes, [Eip8297KeyDerivation.StorageZone], overflowKey.Bytes[33..])));
            Assert.That(db.GetColumnDb(PbtColumns.Storages).GetAllKeys(), Is.EquivalentTo(new[] { headerKey, overflowKey, otherAddressKey }.Select(key => Persisted(SlotRun.RunKey(key)))));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Typed_tombstones_and_whole_code_are_published_atomically(bool commit)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        PbtVariableTreeKey storageKey = PbtTestLeaves.SlotKey(TestItem.AddressA, 0);
        CodeInfo code = new(Bytes.FromHexString("0x6001600255"));
        ValueHash256 codeHash = Keccak.Compute(code.CodeSpan).ValueHash256;
        EvmWord slot = EvmWordSlot.FromStripped(Bytes.FromHexString("0xabcd"));
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        StateId second = new(2, TestItem.KeccakB.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            batch.SetAccount(addressHash, Account.TotallyEmpty.ToPbtAccount());
            batch.SetSlot(storageKey, slot);
            batch.SetCode(codeHash, code);
            batch.Commit();
        }
        Assert.That(() => persistence.CreateWriteBatch(StateId.PreGenesis, second, default, WriteFlags.None), Throws.InvalidOperationException);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(first, second, default, WriteFlags.None))
        {
            batch.SetAccount(addressHash, null);
            batch.SetSlot(storageKey, default);
            if (commit) batch.Commit();
        }

        using IPbtPersistence.IReader reader = new PbtRocksDbPersistence(db, new PbtConfig(), NullTrieNodeLog.Instance).CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(commit ? second : first));
            Assert.That(reader.GetAccount(addressHash), Is.EqualTo(commit ? null : Account.TotallyEmpty.ToPbtAccount()));
            Assert.That(reader.GetSlot(storageKey), Is.EqualTo(commit ? default : slot));
            Assert.That(reader.EnumerateAccounts().Drain().Count, Is.EqualTo(commit ? 0 : 1));
            Assert.That(db.GetColumnDb(PbtColumns.Storages).GetAllKeys().Count(), Is.EqualTo(commit ? 0 : 1));
            Assert.That(reader.GetCode(codeHash), Is.EqualTo(code));
            Assert.That(reader.GetCode(TestItem.KeccakC.ValueHash256), Is.Null);
        }
    }

    [Test]
    public void Whole_group_replacement_drops_omitted_nodes()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        PbtNodePath firstPath = new([0], 1);
        PbtNodePath secondPath = new([0], 2);
        byte[] firstNode = BranchNode(1);
        byte[] secondNode = BranchNode(2);
        StateId firstState = new(1, TestItem.KeccakA.ValueHash256);
        StateId secondState = new(2, TestItem.KeccakB.ValueHash256);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, firstState, default, WriteFlags.None))
        {
            using RefCountingMemory group = EncodeGroup(null, new(firstPath.ToPath<PbtStorageNodePath>(), firstNode), new(secondPath.ToPath<PbtStorageNodePath>(), secondNode));
            batch.SetNodeGroup(PbtTestPaths.Locate(firstPath).GroupKey, group);
            batch.Commit();
        }

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(firstState, secondState, default, WriteFlags.None))
        {
            WriteGroup(batch, secondPath, secondNode);
            batch.Commit();
        }

        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadNode(reader, firstPath), Is.Null);
            Assert.That(ReadNode(reader, secondPath), Is.EqualTo(secondNode));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Group_writes_copy_borrowed_payloads_and_commit_or_discard(bool commit)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        PbtNodePath[] paths = [new(Bytes.FromHexString("8000"), 9), new([], 0), new(Bytes.FromHexString("00"), 5),
            new(Bytes.FromHexString("0100"), 9), new(Bytes.FromHexString("ff00"), 9), new(Bytes.FromHexString("000000"), 17)];
        using IPbtPersistence.IReader olderReader = persistence.CreateReader();
        TrackingMemoryProvider memoryProvider = new();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, new StateId(1, default), default, WriteFlags.None))
        {
            foreach (PbtNodePath path in paths) WriteGroup(batch, path, BranchNode(1), memoryProvider);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            if (commit) batch.Commit();
        }

        using IPbtPersistence.IReader reader = persistence.CreateReader();
        PbtStorageNodePath[] expected = commit ? [new([], 0), new(Bytes.FromHexString("00"), 4), new(Bytes.FromHexString("01"), 8),
            new(Bytes.FromHexString("80"), 8), new(Bytes.FromHexString("ff"), 8), new(Bytes.FromHexString("0000"), 16)] : [];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(olderReader.CurrentState, Is.EqualTo(StateId.PreGenesis));
            foreach (PbtNodePath path in paths) Assert.That(ReadNode(olderReader, path), Is.Null);
            Assert.That(PbtStoreTestExtensions.PersistedNodeGroupKeys(db), Is.EquivalentTo(expected));
            Assert.That(reader.CurrentState, Is.EqualTo(commit ? new StateId(1, default) : StateId.PreGenesis));
            foreach (PbtNodePath path in paths)
                Assert.That(ReadNode(reader, path), commit ? Is.EqualTo(BranchNode(1)) : Is.Null);
        }
    }

    [Test]
    public void Incremental_flush_reopens_with_partial_tail_and_latest_values()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        using MemDb metadata = new();
        PbtConfig config = new() { CompactSize = 2, CompactionOffset = 0 };
        PbtResourcePool pool = new(config);
        PbtSnapshotRepository repository = new(new MetricsConfig());
        ICompactionSchedule schedule = PbtCoreRegistration.CreateCompactionSchedule(metadata, config, LimboLogs.Instance);
        PbtSnapshotCompactor compactor = new(pool, schedule, repository, config, LimboLogs.Instance);
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < 32; index++)
        {
            byte[] key = PbtStoreTestExtensions.ZoneKey("00");
            key[1] = (byte)(index << 3);
            byte[] value = Value((byte)(index + 1));
            changes.Add((key, value));
            oracle.Insert(key, value);
        }
        tree.ApplyBatch(changes);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
        StateId last = StateId.PreGenesis;
        try
        {
            using (ColumnsDb<PbtColumns> db = PbtStoreTestExtensions.OpenPbtRocksDb(dbPath.Path, config))
            {
                PbtRocksDbPersistence persistence = new(db, config, NullTrieNodeLog.Instance);
                PbtPersistenceCoordinator coordinator = new(config, new PbtTestContext.TestFinalizedStateProvider(), persistence,
                    repository, schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
                for (ulong number = 0; number <= 5; number++)
                {
                    StateId next = new(number, TestItem.KeccakA.ValueHash256);
                    PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
                    content.Accounts[addressHash] = new Account(number, number + 10).ToPbtAccount();
                    foreach (PbtPhysicalPayload physical in tree.PhysicalPayloads)
                    {
                        using RefCountingMemory payload = RefCountingMemory.OwningRocksDb(new ArrayMemoryManager(physical.Payload.ToArray()));
                        content.SetNodeGroup(physical.Key, payload);
                    }
                    repository.TryAdd(new PbtSnapshot(last, next, tree.RootHash, content, pool, PbtResourcePool.Usage.MainBlockProcessing));
                    compactor.DoCompactSnapshot(next);
                    last = next;
                }
                coordinator.FlushToPersistence(CancellationToken.None);
                Assert.That(repository.Count, Is.Zero);
                Assert.That(coordinator.CheckPersistence(new StateId(2, TestItem.KeccakA.ValueHash256)), Is.False, "queued IDs behind persistence are harmless");
            }

            using ColumnsDb<PbtColumns> reopenedDb = PbtStoreTestExtensions.OpenPbtRocksDb(dbPath.Path, config);
            using IPbtPersistence.IReader reader = new PbtRocksDbPersistence(reopenedDb, config, NullTrieNodeLog.Instance).CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(last));
                Assert.That(reader.CurrentRoot, Is.EqualTo(tree.RootHash));
                Assert.That(reader.GetAccount(addressHash), Is.EqualTo(new Account(5, 15).ToPbtAccount()));
            }
            List<PbtPhysicalPayload> persisted = [];
            foreach (PbtStorageNodePath groupKey in PbtStoreTestExtensions.PersistedNodeGroupKeys(reopenedDb))
            {
                using RefCountingMemory payload = reader.GetNodeGroup(groupKey)!;
                PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan());
                persisted.Add(new PbtPhysicalPayload(groupKey, payload.GetSpan()));
            }
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(persisted);
            Assert.That(reopened.EnumerateRecords().Count, Is.EqualTo(tree.Nodes.Count));
            byte[] deletedKey = PbtStoreTestExtensions.ZoneKey("0000");
            byte[] replacedKey = PbtStoreTestExtensions.ZoneKey("0008");
            oracle.Delete(deletedKey);
            oracle.Insert(replacedKey, Value(99));
            ValueHash256 updatedRoot = reopened.Fold(reader.CurrentRoot, [(deletedKey, null), (replacedKey, Value(99))]);
            Assert.That(updatedRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(reopened.EnumerateRecords(), Has.Count.EqualTo(30).And.All.Matches<PbtNodeRecord>(record => record.Encoding.Span[0] == 1), "the promoted leaf leaves 30 branches, all with inline leaves");
        }
        finally
        {
            repository.RemoveStatesUntil(ulong.MaxValue);
        }
    }

    /// <summary>Commits state and node groups of every column to RocksDB and reads them back after a reopen.</summary>
    /// <remarks>
    /// Opens with the default <see cref="DbConfig"/> and <see cref="PbtConfig"/> over every column, so it also guards that
    /// rocksdb accepts each column's default options: an option rocksdb does not know fails the database open, which with
    /// pbt enabled is the node failing to start. Keep the defaults here.
    /// </remarks>
    [Test]
    public void NodeGroupsReopenFromRocksDbAsCanonicalNodes()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        PbtConfig pbtConfig = new();
        byte[] widePath = new byte[35];
        widePath[0] = Eip8297KeyDerivation.StorageZone;
        (PbtStorageNodePath Path, PbtColumns Column)[] groups =
        [
            (new PbtStorageNodePath([], 0), PbtColumns.Metadata),
            (new PbtStorageNodePath(Bytes.FromHexString("00"), 4), PbtColumns.TopNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("80"), 4), PbtColumns.TopNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("f0"), 4), PbtColumns.TopNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("00"), 8), PbtColumns.TopNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("01"), 8), PbtColumns.TopNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("ff"), 8), PbtColumns.TopNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("00000000"), 32), PbtColumns.AccountNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("80000000"), 32), PbtColumns.AccountNodeGroups),
            (new PbtStorageNodePath(Bytes.FromHexString("01" + new string('0', 64)), 264), PbtColumns.CodeNodeGroups),
            (new PbtStorageNodePath(widePath, 280), PbtColumns.StorageNodeGroups),
        ];
        PbtStorageNodePath[] expectedPaths = new PbtStorageNodePath[groups.Length];
        for (int index = 0; index < groups.Length; index++) expectedPaths[index] = groups[index].Path;
        byte[] encoding = PbtTreeHarness.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
        StateId state = new(1, TestItem.KeccakA.ValueHash256);
        ValueHash256 treeRoot = TestItem.KeccakD.ValueHash256;
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        Account account = new(7, 9, TestItem.KeccakB, TestItem.KeccakC);
        PbtVariableTreeKey storageKey = PbtTestLeaves.SlotKey(TestItem.AddressA, 64);
        EvmWord slot = EvmWordSlot.FromStripped(TestItem.KeccakD.Bytes);
        CodeInfo code = new(TestItem.KeccakA.Bytes.ToArray());

        using (ColumnsDb<PbtColumns> db = PbtStoreTestExtensions.OpenPbtRocksDb(dbPath.Path, pbtConfig))
        {
            PbtRocksDbPersistence persistence = new(db, pbtConfig, NullTrieNodeLog.Instance);
            using IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, state, treeRoot, WriteFlags.None);
            batch.SetAccount(addressHash, account.ToPbtAccount());
            batch.SetSlot(storageKey, slot);
            batch.SetCode(account.CodeHash.ValueHash256, code);
            foreach ((PbtStorageNodePath path, PbtColumns _) in groups)
                WriteGroup(batch, PbtTestPaths.PathOf(path, NodePosition(path)), encoding);
            batch.Commit();
        }

        using (ColumnsDb<PbtColumns> db = PbtStoreTestExtensions.OpenPbtRocksDb(dbPath.Path, pbtConfig))
        {
            PbtRocksDbPersistence persistence = new(db, pbtConfig, NullTrieNodeLog.Instance);
            using IPbtPersistence.IReader reader = persistence.CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(state));
                Assert.That(reader.CurrentRoot, Is.EqualTo(treeRoot));
                Assert.That(reader.GetAccount(addressHash), Is.EqualTo(account.ToPbtAccount()));
                Assert.That(reader.GetSlot(storageKey), Is.EqualTo(slot));
                Assert.That(reader.GetCode(account.CodeHash.ValueHash256), Is.EqualTo(code));
                Assert.That(PbtStoreTestExtensions.PersistedNodeGroupKeys(db), Is.EquivalentTo(expectedPaths));
                Assert.That(db.GetColumnDb(PbtColumns.Codes).Get(account.CodeHash.Bytes), Is.EqualTo(code.Code.ToArray()));
                Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("validState"u8), Is.EqualTo(new byte[] { 1 }));
            }
            PbtStorageNodePath missing = new(Bytes.FromHexString("00000001"), 32);
            PbtStorageNodePath[] batchPaths = [.. expectedPaths, missing, expectedPaths[0]];
            RefCountingMemory?[] batchPayloads = reader.GetNodeGroups(batchPaths);
            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(reader.GetNodeGroups(Array.Empty<PbtStorageNodePath>()), Is.Empty);
                    Assert.That(batchPayloads, Has.Length.EqualTo(batchPaths.Length));
                    Assert.That(batchPayloads[^2], Is.Null);
                    Assert.That(batchPayloads[^1]!.GetSpan().ToArray(), Is.EqualTo(batchPayloads[0]!.GetSpan().ToArray()));
                }
                for (int index = 0; index < expectedPaths.Length; index++)
                {
                    using RefCountingMemory? single = reader.GetNodeGroup(expectedPaths[index]);
                    Assert.That(batchPayloads[index]!.GetSpan().ToArray(), Is.EqualTo(single!.GetSpan().ToArray()));
                }
            }
            finally
            {
                foreach (RefCountingMemory? payload in batchPayloads) ((IDisposable?)payload)?.Dispose();
            }
            foreach ((PbtStorageNodePath path, PbtColumns column) in groups)
            {
                using RefCountingMemory? payload = reader.GetNodeGroup(path);
                Assert.That(payload, Is.Not.Null, $"group {path.BitDepth}:{Convert.ToHexString(path.ToPathArray())}");
                byte[] storageKeyBytes = path.ToStorageKey(column);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(db.GetColumnDb(column).Get(storageKeyBytes), Is.EqualTo(payload!.GetSpan().ToArray()));
                    Assert.That(ReadNode(reader, PbtTestPaths.PathOf(path, NodePosition(path))), Is.EqualTo(encoding));
                }
            }
        }

        static int NodePosition(PbtStorageNodePath path) => path.BitDepth == 0 ? PbtFourLevelGroupGeometry.RootPosition : 0;
    }

    private static IEnumerable<TestCaseData> NodeGroupLeaseCases()
    {
        foreach ((string prefix, int depth) in new (string, int)[] { ("00", 1), ("0000", 9), ("0000000000", 33) })
            foreach (bool multiGet in new[] { false, true }) yield return new TestCaseData(prefix, depth, multiGet);
    }

    [TestCaseSource(nameof(NodeGroupLeaseCases))]
    public void Node_group_lease_survives_reader_and_persistence_changes_until_disposed(string prefix, int depth, bool multiGet)
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        using ColumnsDb<PbtColumns> db = PbtStoreTestExtensions.OpenPbtRocksDb(dbPath.Path, new PbtConfig());
        PbtRocksDbPersistence persistence = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        PbtNodePath path = new(Bytes.FromHexString(prefix), depth);
        PbtNodePath groupKey = PbtTestPaths.Locate(path).GroupKey;
        byte[] originalNode = BranchNode(1);
        byte[] replacementNode = BranchNode(2);
        RefCountingMemory payload;

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, new StateId(1, default), default, WriteFlags.None))
        {
            WriteGroup(batch, path, originalNode);
            batch.Commit();
        }

        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            payload = multiGet ? reader.GetNodeGroups(new[] { groupKey })[0]! : reader.GetNodeGroup(groupKey)!;
            Assert.That(PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan()).GetEncoding(PbtTestPaths.Locate(path).Position).ToArray(),
                Is.EqualTo(originalNode));
        }

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            new StateId(1, default), new StateId(2, default), default, WriteFlags.None))
        {
            WriteGroup(batch, path, replacementNode);
            batch.Commit();
        }

        Assert.That(PbtStoreTestExtensions.ReadGroup(groupKey, payload.GetSpan()).GetEncoding(PbtTestPaths.Locate(path).Position).ToArray(),
            Is.EqualTo(originalNode));
        ((IDisposable)payload).Dispose();
    }

    [Test]
    public void Default_node_group_batch_owns_each_result_and_releases_partial_results_on_failure([Values] bool fail)
    {
        TrackingMemoryProvider memory = new();
        int calls = 0;
        IPbtPersistence.IReader reader = new DefaultBatchReader(() =>
        {
            if (++calls == 3)
            {
                if (fail) throw new IOException("batch read failed");
                return null;
            }
            RefCountingMemory payload = memory.Rent(1);
            payload.GetSpan()[0] = (byte)calls;
            return payload;
        });
        PbtNodePath path = new(Bytes.FromHexString("00"), 4);
        PbtNodePath[] paths = [path, path, new(Bytes.FromHexString("10"), 4)];
        Assert.That(reader.GetNodeGroups(Array.Empty<PbtNodePath>()), Is.Empty);
        if (fail)
        {
            Assert.That(() => reader.GetNodeGroups(paths), Throws.TypeOf<IOException>());
        }
        else
        {
            RefCountingMemory?[] payloads = reader.GetNodeGroups(paths);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(payloads, Has.Length.EqualTo(3));
                Assert.That(payloads[0]!.GetSpan()[0], Is.EqualTo(1));
                Assert.That(payloads[1]!.GetSpan()[0], Is.EqualTo(2));
                Assert.That(payloads[2], Is.Null);
            }
            foreach (RefCountingMemory? payload in payloads) ((IDisposable?)payload)?.Dispose();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    private sealed class DefaultBatchReader(Func<RefCountingMemory?> read) : IPbtPersistence.IReader
    {
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot => default;
        public PbtAccount? GetAccount(in ValueHash256 addressHash) => throw new NotSupportedException();
        public PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey> => throw new NotSupportedException();
        public CodeInfo? GetCode(in ValueHash256 codeHash) => throw new NotSupportedException();
        public bool TryGetCodeLeaf(in PbtPath key, out ValueHash256 value) => throw new NotSupportedException();
        public IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> EnumerateAccounts() => throw new NotSupportedException();
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => read();
        public void Dispose() { }
    }

    [Test]
    public void Fresh_store_is_stamped_but_unpublished_until_the_first_commit()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");

        PbtRocksDbPersistence persistence = new(db, new PbtConfig(), NullTrieNodeLog.Instance);

        IDb metadata = db.GetColumnDb(PbtColumns.Metadata);
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.Get(SchemaEpochKey), Is.EqualTo(Epoch(23)));
            Assert.That(metadata.Get(CurrentStateKey), Is.Null);
            Assert.That(metadata.Get(ValidStateKey), Is.Null);
            Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
        }
    }

    [Test]
    public void Import_mode_does_not_clear_a_valid_pre_genesis_store()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig { ImportFromPreimageFlat = true }, NullTrieNodeLog.Instance);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis,
            StateId.PreGenesis,
            default,
            WriteFlags.None))
        {
            batch.SetAccount(PbtStateKey.AddressKeyHash(TestItem.AddressA), Account.TotallyEmpty.ToPbtAccount());
            batch.Commit();
        }

        Assert.That(persistence.IsValid, Is.True);
        Assert.That(new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = true }, NullTrieNodeLog.Instance).IsValid, Is.True);
    }

    [Test]
    public void Failed_final_commit_does_not_publish_state_or_validity()
    {
        using SnapshotableMemColumnsDb<PbtColumns> inner = new("pbt");
        FailNextCommitColumnsDb db = new(inner);
        TrackingMemoryProvider memoryProvider = new();
        PbtRocksDbPersistence persistence = new(db, new PbtConfig { ImportFromPreimageFlat = true }, NullTrieNodeLog.Instance);
        ValueHash256 stagedAddressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        using (IPbtPersistence.IWriteBatch staging = persistence.CreateStagingWriteBatch(WriteFlags.None))
        {
            staging.SetAccount(stagedAddressHash, Account.TotallyEmpty.ToPbtAccount());
            staging.Commit();
        }

        db.FailNextCommit = true;
        IPbtPersistence.IWriteBatch final = persistence.CreateWriteBatch(
            StateId.PreGenesis,
            new StateId(7, TestItem.KeccakB.ValueHash256),
            TestItem.KeccakA.ValueHash256,
            WriteFlags.None);
        PbtVariableTreeKey nodeKey = new([0x80]);
        WriteGroup(final, new PbtNodePath([], 0), PbtTreeHarness.EncodeLeaf(nodeKey), memoryProvider);

        Assert.That(() => final.Commit(), Throws.TypeOf<IOException>());
        final.Dispose();
        IDb metadata = inner.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.GetColumnDb(PbtColumns.Accounts).Get(stagedAddressHash.Bytes), Is.Not.Null);
            Assert.That(inner.GetColumnDb(PbtColumns.Metadata).Get("rootNodeGroup"u8), Is.Null);
            Assert.That(metadata.Get(CurrentStateKey), Is.Null);
            Assert.That(metadata.Get(ValidStateKey), Is.Null);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            Assert.That(metadata.Get(SchemaEpochKey), Is.EqualTo(Epoch(23)));
        }
    }

    private static IEnumerable<TestCaseData> InvalidMetadataCases()
    {
        yield return new TestCaseData(Epoch(8), null, null).SetName("Rejects_epoch_8");
        yield return new TestCaseData(Epoch(22), CurrentState(), new byte[] { 1 }).SetName("Rejects_epoch_22");
        yield return new TestCaseData(new byte[] { 9 }, null, null).SetName("Rejects_malformed_epoch");
        yield return new TestCaseData(Epoch(23), new byte[] { 0 }, null).SetName("Rejects_malformed_current_state");
        yield return new TestCaseData(Epoch(23), null, Array.Empty<byte>()).SetName("Rejects_empty_validity");
        yield return new TestCaseData(Epoch(23), null, new byte[] { 2 }).SetName("Rejects_unknown_validity");
        yield return new TestCaseData(Epoch(23), null, new byte[] { 1 }).SetName("Rejects_validity_without_current_state");
        yield return new TestCaseData(Epoch(23), CurrentState(), null).SetName("Rejects_current_state_without_validity");
        yield return new TestCaseData(null, CurrentState(), null).SetName("Rejects_unstamped_current_state");
    }

    [TestCaseSource(nameof(InvalidMetadataCases))]
    public void Invalid_or_inconsistent_metadata_is_rejected_before_node_column_access(
        byte[]? epoch,
        byte[]? currentState,
        byte[]? validity)
    {
        using SnapshotableMemColumnsDb<PbtColumns> inner = new("pbt");
        IDb metadata = inner.GetColumnDb(PbtColumns.Metadata);
        if (epoch is not null) metadata[SchemaEpochKey] = epoch;
        if (currentState is not null) metadata[CurrentStateKey] = currentState;
        if (validity is not null) metadata[ValidStateKey] = validity;
        ThrowOnNodeGroupsDb db = new(inner);

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig(), NullTrieNodeLog.Instance), Throws.TypeOf<InvalidDataException>());
        Assert.That(db.NodeGroupsAccessed, Is.False);
    }

    private static IEnumerable<TestCaseData> PartitionCases()
    {
        yield return new TestCaseData(new PbtNodePath([], 0), PbtColumns.Metadata);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("00"), 5), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("f0"), 5), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("00000000"), 29), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("0000000000"), 33), PbtColumns.AccountNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("8000000000"), 33), PbtColumns.AccountNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("010000000000000000000000000000000000000000000000000000000000000000"), 261), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtNodePath(Bytes.FromHexString("01000000000000000000000000000000000000000000000000000000000000000000"), 265), PbtColumns.CodeNodeGroups);
        yield return new TestCaseData(new PbtStorageNodePath(Bytes.FromHexString("ff0000000000000000000000000000000000000000000000000000000000000000"), 261), PbtColumns.TopNodeGroups);
        yield return new TestCaseData(new PbtStorageNodePath(Bytes.FromHexString("ff000000000000000000000000000000000000000000000000000000000000000000"), 265), PbtColumns.StorageNodeGroups);
        yield return new TestCaseData(new PbtStorageNodePath(Bytes.FromHexString("ff" + new string('0', 130)), 525), PbtColumns.StorageNodeGroups);
    }

    [TestCaseSource(nameof(PartitionCases))]
    public void Partition_groups_replace_and_delete_only_their_physical_record<TPath>(TPath path, PbtColumns column)
        where TPath : struct, IPbtNodePath<TPath>
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtConfig config = new();
        PbtRocksDbPersistence persistence = new(db, config, NullTrieNodeLog.Instance);
        TPath groupKey = PbtTestPaths.Locate(path).GroupKey;
        byte[] physicalKey = groupKey.ToStorageKey(column);
        StateId first = new(1, TestItem.KeccakA.ValueHash256);
        StateId second = new(2, TestItem.KeccakB.ValueHash256);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, first, default, WriteFlags.None))
        {
            WriteGroup(batch, path, BranchNode(1));
            batch.Commit();
        }
        using IPbtPersistence.IReader olderReader = persistence.CreateReader();
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(first, second, default, WriteFlags.None))
        {
            WriteGroup(batch, path, BranchNode(2));
            batch.Commit();
        }
        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ReadNode(reader, path), Is.EqualTo(BranchNode(2)));
                Assert.That(ReadNode(olderReader, path), Is.EqualTo(BranchNode(1)));
                Assert.That(PbtStoreTestExtensions.PersistedNodeGroupKeys(db), Is.EqualTo(new[] { groupKey.ToPath<PbtStorageNodePath>() }));
                Assert.That(db.GetColumnDb(column).Get(physicalKey), Is.Not.Null);
                foreach (PbtColumns otherColumn in new[] { PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups })
                    if (otherColumn != column) Assert.That(db.GetColumnDb(otherColumn).GetAll(), Is.Empty, otherColumn.ToString());
                if (column != PbtColumns.Metadata) Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("rootNodeGroup"u8), Is.Null);
            }
        }
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(second, new StateId(3, default), default, WriteFlags.None))
        {
            batch.SetNodeGroup(groupKey, null);
            batch.Commit();
        }
        using IPbtPersistence.IReader deletedReader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadNode(deletedReader, path), Is.Null);
            Assert.That(PbtStoreTestExtensions.PersistedNodeGroupKeys(db), Is.Empty);
            Assert.That(db.GetColumnDb(column).Get(physicalKey), Is.Null);
            Assert.That(ReadNode(olderReader, path), Is.EqualTo(BranchNode(1)));
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get(SchemaEpochKey), Is.EqualTo(Epoch(23)));
        }
    }

    [Test]
    public void Populated_column_is_detected_in_every_location([Values] PbtColumns column, [Values] bool stamped)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        byte[] key = new PbtNodePath(Bytes.FromHexString("00"), 4).ToStorageKey(column);
        db.GetColumnDb(column).Set(key, Bytes.FromHexString("01"));
        if (stamped)
        {
            db.GetColumnDb(PbtColumns.Metadata).Set(SchemaEpochKey, Epoch(23));
        }

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig(), NullTrieNodeLog.Instance),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains(stamped ? "interrupted initialization" : "no schema epoch"));
        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { ImportFromPreimageFlat = true }, NullTrieNodeLog.Instance),
            stamped ? Throws.Nothing : Throws.TypeOf<InvalidDataException>().With.Message.Contains("no schema epoch"));
        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig { MigrationSnapshotPath = "snapshot.pbt" }, NullTrieNodeLog.Instance),
            stamped ? Throws.Nothing : Throws.TypeOf<InvalidDataException>().With.Message.Contains("no schema epoch"));
        Assert.That(db.GetColumnDb(column).Get(key), Is.EqualTo(Bytes.FromHexString("01")));
    }

    private static RefCountingMemory EncodeGroup(IRefCountingMemoryProvider? memoryProvider, params PbtNodeRecord[] records) =>
        PbtNodeGroupEncoder.EncodeToMemory(PbtTestPaths.Locate(records[0].Path).GroupKey, records, memoryProvider ?? PooledRefCountingMemoryProvider.Instance);

    private static void WriteGroup<TPath>(IPbtPersistence.IWriteBatch batch, TPath path, byte[] node, IRefCountingMemoryProvider? memoryProvider = null)
        where TPath : struct, IPbtNodePath<TPath>
    {
        using RefCountingMemory payload = EncodeGroup(memoryProvider, new PbtNodeRecord(path.ToPath<PbtStorageNodePath>(), node));
        batch.SetNodeGroup(PbtTestPaths.Locate(path).GroupKey, payload);
    }

    private static byte[]? ReadNode<TPath>(IPbtPersistence.IReader reader, TPath path)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupLocation<TPath> location = PbtTestPaths.Locate(path);
        using RefCountingMemory? payload = reader.GetNodeGroup(location.GroupKey);
        if (payload is null) return null;
        ReadOnlySpan<byte> encoding = PbtStoreTestExtensions.ReadGroup(location.GroupKey, payload.GetSpan()).GetEncoding(location.Position).Span;
        return encoding.IsEmpty ? null : encoding.ToArray();
    }

    private static byte[] BranchNode(byte marker) => PbtTreeHarness.EncodeBranch(
        Bytes.FromHexString("80"), 1,
        new ValueHash256(Value(marker)),
        new ValueHash256(Value((byte)(marker + 1))));

    private static byte[] Persisted<TKey>(in TKey key) where TKey : struct, IPbtKey<TKey> => PbtStorageKeyLayout.Encode(key, new byte[TKey.Capacity]).ToArray();

    private static byte[] Epoch(int epoch)
    {
        byte[] value = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(value, epoch);
        return value;
    }

    private static byte[] CurrentState()
    {
        byte[] value = new byte[sizeof(ulong) + 2 * ValueHash256.MemorySize];
        BinaryPrimitives.WriteUInt64BigEndian(value, 1);
        return value;
    }

    private sealed class FailNextCommitColumnsDb(IColumnsDb<PbtColumns> inner) : IColumnsDb<PbtColumns>
    {
        public bool FailNextCommit { get; set; }
        public IEnumerable<PbtColumns> ColumnKeys => inner.ColumnKeys;
        public long EstimatedCount => inner.EstimatedCount;
        public IDb GetColumnDb(PbtColumns key) => inner.GetColumnDb(key);
        public IColumnDbSnapshot<PbtColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() => inner.Dispose();

        public IColumnsWriteBatch<PbtColumns> StartWriteBatch()
        {
            IColumnsWriteBatch<PbtColumns> batch = inner.StartWriteBatch();
            if (!FailNextCommit) return batch;
            FailNextCommit = false;
            return new ThrowOnDisposeBatch(batch);
        }

        private sealed class ThrowOnDisposeBatch(IColumnsWriteBatch<PbtColumns> innerBatch) : IColumnsWriteBatch<PbtColumns>
        {
            public IWriteBatch GetColumnBatch(PbtColumns key) => innerBatch.GetColumnBatch(key);
            public void Clear() => innerBatch.Clear();
            public void Dispose()
            {
                innerBatch.Clear();
                innerBatch.Dispose();
                throw new IOException("commit failed");
            }
        }
    }

    private sealed class ThrowOnNodeGroupsDb(IColumnsDb<PbtColumns> inner) : IColumnsDb<PbtColumns>
    {
        public bool NodeGroupsAccessed { get; private set; }
        public IEnumerable<PbtColumns> ColumnKeys => inner.ColumnKeys;
        public long EstimatedCount => inner.EstimatedCount;

        public IDb GetColumnDb(PbtColumns key)
        {
            if (key is PbtColumns.AccountNodeGroups or PbtColumns.CodeNodeGroups or PbtColumns.StorageNodeGroups)
            {
                NodeGroupsAccessed = true;
                throw new AssertionException("Node groups were accessed before metadata rejection.");
            }
            return inner.GetColumnDb(key);
        }

        public IColumnsWriteBatch<PbtColumns> StartWriteBatch() => inner.StartWriteBatch();
        public IColumnDbSnapshot<PbtColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() => inner.Dispose();
    }
}

[TestFixture]
public class PbtProtocolStorageClearTests
{
    public enum History { InitDestroy, InitRevert, LaterTransactionDestroy, Create2Recreate, RevertedParent }
    private static Address Sender => TestItem.PrivateKeyA.Address;
    private static Address Factory => TestItem.AddressC;
    private static readonly UInt256 InitialBalance = 1000.Ether;

    [Test]
    public async Task Protocol_storage_clear_survives_retention_and_base_reopen(
        [Values] History history, [Values] bool retained)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SnapshotableMemColumnsDb<PbtColumns> baseDb = new("protocol-pbt");
        using MemDb catalog = new();
        using MemDb codeDb = new();
        byte[] writes = Prepare.EvmCode.PushData(0xAB).PushData(5).Op(Instruction.SSTORE)
            .PushData(0x1234).PushData(1000).Op(Instruction.SSTORE).Done;
        byte[] destroy = Prepare.EvmCode.SELFDESTRUCT(Sender).Done;
        byte[] childInit = [.. writes, .. Prepare.EvmCode.ForInitOf(destroy).Done];
        byte[] salt = new UInt256(123).ToBigEndian();
        Address contract = history is History.Create2Recreate or History.RevertedParent
            ? ContractAddress.From(Factory, salt, childInit) : ContractAddress.From(Sender, 0);
        byte[] create = Prepare.EvmCode.Create2(childInit, salt, 0).Done;
        byte[] call = Prepare.EvmCode.Call(contract, 200000).Done;
        int stop = create.Length + 5 + call.Length + 1;
        byte[] factoryCode = history == History.RevertedParent
            ? [.. create, .. call, .. Prepare.EvmCode.Revert(0, 0).Done]
            : [.. create, (byte)Instruction.CALLDATASIZE, (byte)Instruction.PUSH2, (byte)(stop >> 8), (byte)stop,
                (byte)Instruction.JUMPI, .. call, (byte)Instruction.STOP, (byte)Instruction.JUMPDEST, (byte)Instruction.STOP];
        PbtConfig config = new()
        {
            Enabled = true, InlineCompaction = true, CompactSize = 2, CompactionOffset = 0,
            MinReorgDepth = 128, MaxInMemoryBaseSnapshotCount = int.MaxValue,
            ArenaFileSizeBytes = 1048576, PersistedSnapshotDedicatedArenaThresholdBytes = 1048576,
            PersistedSnapshotArenaPageCacheBytes = 0, ValidatePersistedSnapshot = true,
        };
        IContainer Open() => PbtTestContext.BuildProductionContainer(config, builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance));
            builder.RegisterInstance(baseDb).As<IColumnsDb<PbtColumns>>().ExternallyOwned();
            builder.RegisterInstance(catalog).Keyed<IDb>(PbtSnapshotCatalog.DatabaseKey).ExternallyOwned();
            builder.RegisterInstance(codeDb).Keyed<IDb>(DbNames.Code).ExternallyOwned();
        }, new InitConfig { BaseDbPath = directory.Path });
        Hash256 finalRoot;
        BlockHeader head;
        ulong expectedNonce = history is History.Create2Recreate or History.LaterTransactionDestroy ? 2ul : 1ul;
        bool hasFactory = history is History.Create2Recreate or History.RevertedParent;
        bool survives = history is History.LaterTransactionDestroy or History.Create2Recreate;
        await using (IContainer container = Open())
        {
            IWorldStateManager manager = container.Resolve<IWorldStateManager>();
            using ILifetimeScope processing = container.BeginLifetimeScope(builder =>
                builder.AddSingleton<IWorldStateScopeProvider>(manager.GlobalWorldState));
            IWorldState state = processing.Resolve<IWorldState>();
            ITransactionProcessor processor = processing.Resolve<ITransactionProcessor>();
            using (state.BeginScope(IWorldState.PreGenesis))
            {
                state.CreateAccount(Sender, InitialBalance);
                if (hasFactory)
                {
                    state.CreateAccount(Factory, 0, 1);
                    state.InsertCode(Factory, factoryCode, Prague.Instance);
                }
                state.Commit(Prague.Instance);
                state.CommitTree(0);
                head = Header(0, state.StateRoot);
            }
            manager.FlushCache(CancellationToken.None);
            for (ulong block = 1; block <= 2; block++)
            {
                using (state.BeginScope(head))
                {
                    BlockHeader execution = Header(block, head.StateRoot!);
                    if (block == 1)
                    {
                        if (hasFactory) Execute(processor, execution, Factory, [], 0, history != History.RevertedParent, 2, true, history == History.RevertedParent);
                        else
                        {
                            byte[] init = history switch
                            {
                                History.InitDestroy => [.. writes, .. destroy],
                                History.InitRevert => [.. writes, .. Prepare.EvmCode.Revert(0, 0).Done],
                                _ => [.. writes, .. Prepare.EvmCode.ForInitOf(destroy).Done],
                            };
                            Execute(processor, execution, null, init, 0, history != History.InitRevert, 2,
                                history == History.InitDestroy, history == History.InitRevert);
                            if (history == History.LaterTransactionDestroy) Execute(processor, execution, contract, [], 1, true, 0, true, false);
                        }
                    }
                    else if (history == History.Create2Recreate) Execute(processor, execution, Factory, [1], 1, true, 2, false, false);
                    AssertFinalEntries(state, contract, survives && (history != History.Create2Recreate || block == 2));
                    if (block == 2)
                    {
                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(state.GetNonce(Sender), Is.EqualTo(expectedNonce));
                            Assert.That(state.GetBalance(Sender), Is.EqualTo(InitialBalance));
                            if (hasFactory) Assert.That(state.GetNonce(Factory), Is.EqualTo(history == History.Create2Recreate ? 3ul : 1ul));
                            if (survives)
                            {
                                Assert.That(state.GetNonce(contract), Is.EqualTo(1ul));
                                Assert.That(state.GetCode(contract).ToArray(), Is.EqualTo(destroy));
                            }
                        }
                    }
                    state.Commit(Prague.Instance);
                    state.CommitTree(block);
                    head = Header(block, state.StateRoot);
                }
                if (retained)
                {
                    PbtSnapshotRepository repository = container.Resolve<PbtSnapshotRepository>();
                    Assert.That(repository.TryLeaseMemoryState(new StateId(head), SnapshotTier.InMemoryBase, out PbtSnapshot? snapshot), Is.True);
                    using (snapshot)
                    {
                        Assert.That(container.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot!), Is.True);
                        repository.RemoveMemorySource(snapshot!);
                    }
                    if (repository.TryLeaseMemoryState(new StateId(head), SnapshotTier.InMemoryCompacted, out PbtSnapshot? compacted))
                        using (compacted) repository.RemoveMemorySource(compacted!);
                }
            }
            finalRoot = head.StateRoot!;
            if (retained)
                Assert.That(((PbtRetainedSnapshotCompactor)container.Resolve<IPbtRetainedSnapshotCompactor>()).DoCompactCompactSized(new StateId(head)), Is.True);
            else manager.FlushCache(CancellationToken.None);
        }
        if (survives) Assert.That(codeDb[ValueKeccak.Compute(destroy).Bytes], Is.EqualTo(destroy));
        (Hash256 cleanRoot, string[] expectedRecords) = await ExpectedRoot(history, expectedNonce, factoryCode, destroy, contract, survives);
        if (!retained && history == History.Create2Recreate)
        {
            string[] actualRecords = Canonical(baseDb);
            TestContext.Out.WriteLine($"factoryCodeHash={ValueKeccak.Compute(factoryCode)} childCodeHash={ValueKeccak.Compute(destroy)}");
            TestContext.Out.WriteLine("ACTUAL ONLY: " + string.Join("\n", actualRecords.Except(expectedRecords)));
            TestContext.Out.WriteLine("EXPECTED ONLY: " + string.Join("\n", expectedRecords.Except(actualRecords)));
        }
        Assert.That(finalRoot, Is.EqualTo(cleanRoot), "root is rebuilt from the expected surviving state, not transient execution snapshots");
        await using (IContainer reopened = Open())
        {
            IWorldStateManager manager = reopened.Resolve<IWorldStateManager>();
            using ILifetimeScope processing = reopened.BeginLifetimeScope(builder =>
                builder.AddSingleton<IWorldStateScopeProvider>(manager.GlobalWorldState));
            IWorldState state = processing.Resolve<IWorldState>();
            using (state.BeginScope(head)) AssertFinalEntries(state, contract, survives);
            manager.FlushCache(CancellationToken.None);
            using IPbtPersistence.IReader reader = reopened.Resolve<IPbtPersistence>().CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(new StateId(head)));
                Assert.That(reader.CurrentRoot, Is.EqualTo(cleanRoot.ValueHash256));
                Assert.That(reopened.Resolve<PbtSnapshotRepository>().GetLastCommittedStateId(), Is.Null, "restart flush works without a new commit");
            }
        }
        await using (IContainer final = Open())
        {
            IWorldStateManager manager = final.Resolve<IWorldStateManager>();
            using ILifetimeScope processing = final.BeginLifetimeScope(builder =>
                builder.AddSingleton<IWorldStateScopeProvider>(manager.GlobalWorldState));
            using (processing.Resolve<IWorldState>().BeginScope(head)) AssertFinalEntries(processing.Resolve<IWorldState>(), contract, survives);
        }
    }

    private static BlockHeader Header(ulong number, Hash256 root) => Build.A.BlockHeader
        .WithNumber(number).WithStateRoot(root).WithGasLimit(8000000).WithBaseFee(0).WithBeneficiary(Sender).TestObject;

    private static void Execute(ITransactionProcessor processor, BlockHeader header, Address? to, byte[] code, ulong nonce,
        bool success, int stores, bool destroyed, bool reverted)
    {
        Transaction transaction = Build.A.Transaction.WithData(code).To(to).WithNonce(nonce)
            .WithValue(0).WithGasLimit(1000000).WithGasPrice(0).WithSenderAddress(Sender).TestObject;
        using ProtocolTracer tracer = new();
        TransactionResult result = processor.Execute(transaction, header, tracer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True, result.ToString());
            Assert.That(tracer.Success, Is.EqualTo(success), tracer.Error);
            Assert.That(tracer.Stores, Is.EqualTo(stores), tracer.Error);
            Assert.That(tracer.Destroyed, Is.EqualTo(destroyed), tracer.Error);
            Assert.That(tracer.Reverted, Is.EqualTo(reverted), tracer.Error);
        }
    }

    private sealed class ProtocolTracer : TxTracer
    {
        internal bool? Success { get; private set; }
        internal string? Error { get; private set; }
        internal int Stores { get; private set; }
        internal bool Destroyed { get; private set; }
        internal bool Reverted { get; private set; }
        internal ProtocolTracer() { IsTracingReceipt = true; IsTracingInstructions = true; }
        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) => Success = true;
        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) { Success = false; Error = error; }
        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            if (opcode == Instruction.SSTORE) Stores++;
            if (opcode == Instruction.SELFDESTRUCT) Destroyed = true;
            if (opcode == Instruction.REVERT) Reverted = true;
        }
    }

    private static void AssertFinalEntries(IWorldState state, Address contract, bool survives)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.AccountExists(contract), Is.EqualTo(survives));
            Assert.That(state.Get(new StorageCell(contract, 5)), Is.EqualTo(survives ? (UInt256)0xAB : UInt256.Zero));
            Assert.That(state.Get(new StorageCell(contract, 1000)), Is.EqualTo(survives ? (UInt256)0x1234 : UInt256.Zero));
        }
    }

    private static async Task<(Hash256, string[])> ExpectedRoot(History history, ulong senderNonce, byte[] factoryCode,
        byte[] contractCode, Address contract, bool survives)
    {
        await using IContainer container = PbtTestContext.BuildProductionContainer(
            new PbtConfig { Enabled = true, EnableLongFinality = false },
            builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance)));
        using ILifetimeScope processing = container.BeginLifetimeScope(builder =>
            builder.AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));
        IWorldState state = processing.Resolve<IWorldState>();
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(Sender, InitialBalance, senderNonce);
            if (history is History.Create2Recreate or History.RevertedParent)
            {
                state.CreateAccount(Factory, 0, history == History.Create2Recreate ? 3ul : 1ul);
                state.InsertCode(Factory, factoryCode, Prague.Instance);
            }
            if (survives)
            {
                state.CreateAccount(contract, 0, 1);
                state.InsertCode(contract, contractCode, Prague.Instance);
                state.Set(new StorageCell(contract, 5), (UInt256)0xAB);
                state.Set(new StorageCell(contract, 1000), (UInt256)0x1234);
            }
            state.Commit(Prague.Instance);
            state.CommitTree(2);
            container.Resolve<IWorldStateManager>().FlushCache(CancellationToken.None);
            return (state.StateRoot, Canonical(container.Resolve<IColumnsDb<PbtColumns>>()));
        }
    }

    private static string[] Canonical(IColumnsDb<PbtColumns> db)
    {
        List<PbtPhysicalPayload> payloads = [];
        foreach (PbtColumns column in new[] { PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups })
            foreach (KeyValuePair<byte[], byte[]> entry in db.GetColumnDb(column).GetAll())
                payloads.Add(new(PbtNodeGroupKey.Decode(entry.Key), entry.Value));
        if (db.GetColumnDb(PbtColumns.Metadata)["rootNodeGroup"u8] is byte[] root) payloads.Add(new(default, root));
        using PbtNodeGroupStore store = PbtNodeGroupStore.FromPhysicalPayloads(payloads);
        return store.CanonicalRecords();
    }
}
