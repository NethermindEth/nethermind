// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;
using Paprika.Data;
using Paprika.Store;
using PaprikaBatch = Paprika.IBatch;
using PaprikaCommitOptions = Paprika.CommitOptions;
using PaprikaKeccak = Paprika.Crypto.Keccak;
using PaprikaReadOnlyBatch = Paprika.IReadOnlyBatch;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class PaprikaFlatPersistenceTests
{
    [Test]
    public void CanWriteAndReadAccountAndStorage()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        PaprikaFlatPersistence persistence = new(columnsDb, paprikaDb);
        Account account = TestItem.GenerateIndexedAccount(0);
        Address address = TestItem.AddressA;
        UInt256 slot = 42;
        SlotValue stored = SlotValue.FromSpanWithoutLeadingZero([0x42]);

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, account);
            writer.SetStorage(address, in slot, stored);
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue actual = default;

        Assert.That(reader.GetAccount(address), Is.EqualTo(account));
        Assert.That(reader.TryGetSlot(address, in slot, ref actual), Is.True);
        Assert.That(actual.ToEvmBytes(), Is.EqualTo(stored.ToEvmBytes()));
        Assert.That(BasePersistence.ReadLayout(columnsDb.GetColumnDb(FlatDbColumns.Metadata)), Is.EqualTo(FlatLayout.PaprikaFlat));
    }

    [Test]
    public void CanRemoveAccountAndStorage()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        PaprikaFlatPersistence persistence = new(columnsDb, paprikaDb);
        Address address = TestItem.AddressA;
        UInt256 slot = 42;

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, TestItem.GenerateIndexedAccount(0));
            writer.SetStorage(address, in slot, SlotValue.FromSpanWithoutLeadingZero([0x42]));
        }

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, null);
            writer.SetStorage(address, in slot, null);
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue actual = default;

        Assert.That(reader.GetAccount(address), Is.Null);
        Assert.That(reader.TryGetSlot(address, in slot, ref actual), Is.False);
    }

    [Test]
    public void RemovingAbsentAccountAndStorageDoesNotAllocatePaprikaPages()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        PaprikaFlatPersistence persistence = new(columnsDb, paprikaDb);
        Address address = TestItem.AddressA;
        UInt256 slot = 42;

        uint nextFreePageBefore = paprikaDb.NextFreePage;
        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetAccount(address, null);
            writer.SetStorage(address, in slot, null);
        }

        Assert.That(paprikaDb.NextFreePage, Is.EqualTo(nextFreePageBefore));
    }

    [Test]
    public void SelfDestructRemovesStorage()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        PaprikaFlatPersistence persistence = new(columnsDb, paprikaDb);
        Address address = TestItem.AddressA;
        UInt256 firstSlot = 42;
        UInt256 secondSlot = 43;

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SetStorage(address, in firstSlot, SlotValue.FromSpanWithoutLeadingZero([0x42]));
            writer.SetStorage(address, in secondSlot, SlotValue.FromSpanWithoutLeadingZero([0x43]));
        }

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None))
        {
            writer.SelfDestruct(address);
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        SlotValue actual = default;

        Assert.That(reader.TryGetSlot(address, in firstSlot, ref actual), Is.False);
        Assert.That(reader.TryGetSlot(address, in secondSlot, ref actual), Is.False);
    }

    [Test]
    public void ReaderUsesPersistedStateRoot()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        PaprikaFlatPersistence persistence = new(columnsDb, paprikaDb);
        Address address = TestItem.AddressA;
        Account firstAccount = TestItem.GenerateIndexedAccount(1);
        Account secondAccount = TestItem.GenerateIndexedAccount(2);
        StateId firstState = new(1, ValueKeccak.Compute("first"));
        StateId secondState = new(2, ValueKeccak.Compute("second"));

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, firstState, WriteFlags.None))
        {
            writer.SetAccount(address, firstAccount);
        }

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(firstState, secondState, WriteFlags.None))
        {
            writer.SetAccount(address, secondAccount);
        }

        using (IColumnsWriteBatch<FlatDbColumns> metadataBatch = columnsDb.StartWriteBatch())
        {
            BasePersistence.SetCurrentState(metadataBatch.GetColumnBatch(FlatDbColumns.Metadata), firstState);
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();

        Assert.That(reader.CurrentState, Is.EqualTo(firstState));
        Assert.That(reader.GetAccount(address), Is.EqualTo(firstAccount));
    }

    [Test]
    public void ReaderRecoversCurrentState_WhenPaprikaCommittedAndMetadataPending()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        PaprikaFlatPersistence persistence = new(columnsDb, paprikaDb);
        Address address = TestItem.AddressA;
        Account firstAccount = TestItem.GenerateIndexedAccount(1);
        Account secondAccount = TestItem.GenerateIndexedAccount(2);
        StateId firstState = new(1, ValueKeccak.Compute("first"));
        StateId secondState = new(2, ValueKeccak.Compute("second"));

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, firstState, WriteFlags.None))
        {
            writer.SetAccount(address, firstAccount);
        }

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(firstState, secondState, WriteFlags.None))
        {
            writer.SetAccount(address, secondAccount);
        }

        using (IColumnsWriteBatch<FlatDbColumns> metadataBatch = columnsDb.StartWriteBatch())
        {
            IWriteBatch metadata = metadataBatch.GetColumnBatch(FlatDbColumns.Metadata);
            BasePersistence.SetCurrentState(metadata, firstState);
            PaprikaFlatPersistence.SetPendingCurrentState(metadata, secondState);
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();

        Assert.That(reader.CurrentState, Is.EqualTo(secondState));
        Assert.That(reader.GetAccount(address), Is.EqualTo(secondAccount));
        Assert.That(PaprikaFlatPersistence.ReadPendingCurrentState(columnsDb.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
    }

    [Test]
    public void ReaderFailsClosed_WhenFlatBatchCommittedButPaprikaDidNotCommit()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        PaprikaFlatPersistence persistence = new(columnsDb, paprikaDb);
        StateId firstState = new(1, ValueKeccak.Compute("first"));
        StateId secondState = new(2, ValueKeccak.Compute("second"));
        TreePath path = TreePath.FromHexString("123456");
        byte[] firstRlp = [0xc1, 0x01];
        byte[] secondRlp = [0xc1, 0x02];

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, firstState, WriteFlags.None))
        {
            writer.SetStateTrieNode(in path, firstRlp);
        }

        using IColumnDbSnapshot<FlatDbColumns> snapshot = columnsDb.CreateSnapshot();
        using (IColumnsWriteBatch<FlatDbColumns> metadataBatch = columnsDb.StartWriteBatch())
        {
            BaseTriePersistence.WriteBatch trieWriteBatch = new(
                (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.StateTopNodes),
                (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.StateNodes),
                (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.StorageNodes),
                (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.FallbackNodes),
                metadataBatch.GetColumnBatch(FlatDbColumns.StateTopNodes),
                metadataBatch.GetColumnBatch(FlatDbColumns.StateNodes),
                metadataBatch.GetColumnBatch(FlatDbColumns.StorageNodes),
                metadataBatch.GetColumnBatch(FlatDbColumns.FallbackNodes),
                WriteFlags.None);
            trieWriteBatch.SetStateTrieNode(in path, secondRlp);
            PaprikaFlatPersistence.SetPendingCurrentState(metadataBatch.GetColumnBatch(FlatDbColumns.Metadata), secondState);
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => persistence.CreateReader())!;
        Assert.That(exception.Message, Does.Contain(nameof(FlatLayout.PaprikaFlat)));
    }

    [Test]
    public async Task ReaderWaits_WhenCommitIsBetweenPendingMetadataAndPaprikaCommit()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        ControlledPaprikaDb controlledPaprikaDb = new(paprikaDb);
        PaprikaFlatPersistence persistence = new(columnsDb, controlledPaprikaDb);
        Address address = TestItem.AddressA;
        Account firstAccount = TestItem.GenerateIndexedAccount(1);
        Account secondAccount = TestItem.GenerateIndexedAccount(2);
        StateId firstState = new(1, ValueKeccak.Compute("first"));
        StateId secondState = new(2, ValueKeccak.Compute("second"));

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, firstState, WriteFlags.None))
        {
            writer.SetAccount(address, firstAccount);
        }

        controlledPaprikaDb.BlockNextCommit();
        IPersistence.IWriteBatch blockedWriter = persistence.CreateWriteBatch(firstState, secondState, WriteFlags.None);
        blockedWriter.SetAccount(address, secondAccount);

        Task commitTask = Task.Run(blockedWriter.Dispose);
        Assert.That(controlledPaprikaDb.CommitEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);

        Task<IPersistence.IPersistenceReader> readerTask = Task.Run(() => persistence.CreateReader());

        try
        {
            Assert.That(readerTask.Wait(TimeSpan.FromMilliseconds(200)), Is.False);

            controlledPaprikaDb.ReleaseCommit.Set();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
            using IPersistence.IPersistenceReader reader = await readerTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(reader.CurrentState, Is.EqualTo(secondState));
            Assert.That(reader.GetAccount(address), Is.EqualTo(secondAccount));
        }
        finally
        {
            controlledPaprikaDb.ReleaseCommit.Set();
        }
    }

    [TestCase(PaprikaFlatCommitMode.FlushDataAndRoot, WriteFlags.None, PaprikaCommitOptions.FlushDataAndRoot)]
    [TestCase(PaprikaFlatCommitMode.FlushDataOnly, WriteFlags.None, PaprikaCommitOptions.FlushDataOnly)]
    [TestCase(PaprikaFlatCommitMode.FlushDataOnly, WriteFlags.DisableWAL, PaprikaCommitOptions.DangerNoFlush)]
    public void CommitUsesConfiguredPaprikaCommitMode(
        PaprikaFlatCommitMode commitMode,
        WriteFlags writeFlags,
        PaprikaCommitOptions expectedCommitOptions)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        using PagedDb paprikaDb = PagedDb.NativeMemoryDb(16 * 1024 * 1024, 2);
        ControlledPaprikaDb controlledPaprikaDb = new(paprikaDb);
        PaprikaFlatPersistence persistence = new(columnsDb, controlledPaprikaDb, commitMode);

        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, writeFlags))
        {
            writer.SetAccount(TestItem.AddressA, TestItem.GenerateIndexedAccount(0));
        }

        Assert.That(controlledPaprikaDb.LastCommitOptions, Is.EqualTo(expectedCommitOptions));
    }

    private sealed class ControlledPaprikaDb(Paprika.IDb inner) : Paprika.IDb
    {
        private int _blockNextCommit;

        public ManualResetEventSlim CommitEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseCommit { get; } = new(false);
        public PaprikaCommitOptions? LastCommitOptions { get; private set; }

        public int HistoryDepth => inner.HistoryDepth;

        public void BlockNextCommit()
        {
            CommitEntered.Reset();
            ReleaseCommit.Reset();
            Volatile.Write(ref _blockNextCommit, 1);
        }

        public PaprikaBatch BeginNextBatch() => new ControlledBatch(inner.BeginNextBatch(), this);

        public PaprikaReadOnlyBatch BeginReadOnlyBatch(string name = "") => inner.BeginReadOnlyBatch(name);

        public PaprikaReadOnlyBatch BeginReadOnlyBatch(in PaprikaKeccak stateHash, string name = "") =>
            inner.BeginReadOnlyBatch(in stateHash, name);

        public void Flush() => inner.Flush();

        public PaprikaReadOnlyBatch BeginReadOnlyBatchOrLatest(in PaprikaKeccak stateHash, string name = "") =>
            inner.BeginReadOnlyBatchOrLatest(in stateHash, name);

        public PaprikaReadOnlyBatch[] SnapshotAll() => inner.SnapshotAll();

        public bool HasState(in PaprikaKeccak keccak) => inner.HasState(in keccak);

        public void ForceFlush() => inner.ForceFlush();

        private void RecordCommitOptions(PaprikaCommitOptions options) => LastCommitOptions = options;

        private bool ShouldBlockCommit() => Interlocked.Exchange(ref _blockNextCommit, 0) == 1;

        private sealed class ControlledBatch(PaprikaBatch innerBatch, ControlledPaprikaDb owner) : PaprikaBatch
        {
            public Metadata Metadata => innerBatch.Metadata;

            public bool TryGet(scoped in Key key, out ReadOnlySpan<byte> result) =>
                innerBatch.TryGet(in key, out result);

            public void VerifyNoPagesMissing() => innerBatch.VerifyNoPagesMissing();

            public void Dispose() => innerBatch.Dispose();

            public void SetMetadata(uint blockNumber, in PaprikaKeccak blockHash) =>
                innerBatch.SetMetadata(blockNumber, in blockHash);

            public void SetRaw(in Key key, ReadOnlySpan<byte> rawData) =>
                innerBatch.SetRaw(in key, rawData);

            public void Destroy(in NibblePath account) =>
                innerBatch.Destroy(in account);

            public void DeleteByPrefix(in Key prefix) =>
                innerBatch.DeleteByPrefix(in prefix);

            public ValueTask Commit(PaprikaCommitOptions options)
            {
                owner.RecordCommitOptions(options);
                if (owner.ShouldBlockCommit())
                {
                    owner.CommitEntered.Set();
                    owner.ReleaseCommit.Wait();
                }

                return innerBatch.Commit(options);
            }

            public void VerifyDbPagesOnCommit() => innerBatch.VerifyDbPagesOnCommit();
        }
    }
}
