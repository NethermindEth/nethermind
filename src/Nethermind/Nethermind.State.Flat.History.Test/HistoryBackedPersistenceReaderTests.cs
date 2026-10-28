// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryBackedPersistenceReaderTests
{
    private static readonly Address Address = new("0x0000000000000000000000000000000000000abc");
    private static readonly Address ContractAddress = new("0x0000000000000000000000000000000000000c0d");
    private static readonly UInt256 Slot = 7;
    private const long MaxEmptyClearsCacheBytes = 64;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 5, [0xAA]);

        for (ulong block = 0; block <= 10; block++)
        {
            HistoryColumnsWriter.MarkBlock(_historyColumns, block, Keccak.EmptyTreeHash);
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 10);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public void Resolves_account_as_of_pinned_block()
    {
        Account? present = Reader(10).GetAccount(Address);
        Account? absent = Reader(3).GetAccount(Address);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(present, Is.Not.Null);
            Assert.That(present!.Nonce, Is.EqualTo((ulong)5));
            Assert.That(present.Balance, Is.EqualTo((UInt256)500));
            Assert.That(absent, Is.Null);
        }
    }

    [Test]
    public void Resolves_storage_as_of_pinned_block()
    {
        UInt256 present = default;
        UInt256 absent = default;
        bool foundPresent = Reader(10).TryGetSlot(Address, Slot, ref present);
        bool foundAbsent = Reader(3).TryGetSlot(Address, Slot, ref absent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundPresent, Is.True);
            Assert.That(present.ToMinimalBigEndian(), Is.EqualTo(new byte[] { 0xAA }));
            Assert.That(foundAbsent, Is.False);
        }
    }

    [Test]
    public void Corrupt_account_row_surfaces_as_missing_trie_node()
    {
        HistoryColumnsWriter.RecordRawAccountRow(_historyColumns, Address, 6, new byte[300]);

        Assert.That(() => Reader(10).GetAccount(Address),
            Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.TypeOf<StateUnavailableException>(),
            "a corrupt row is not state the node legitimately lacks; the exact base type keeps it on the -32000 + WARN path");
    }

    [Test]
    public void Corrupt_storage_row_surfaces_as_missing_trie_node()
    {
        HistoryColumnsWriter.RecordRawStorageRow(_historyColumns, Address, Slot, 6, new byte[300]);

        Assert.That(() =>
        {
            UInt256 value = default;
            Reader(10).TryGetSlot(Address, Slot, ref value);
        }, Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.TypeOf<StateUnavailableException>(),
            "a corrupt row is not state the node legitimately lacks; the exact base type keeps it on the -32000 + WARN path");
    }

    [Test]
    public void Pins_current_state_to_its_block() =>
        Assert.That(Reader(10).CurrentState.BlockNumber, Is.EqualTo(10));

    [Test]
    public void Unsupported_members_throw_not_supported()
    {
        HistoryBackedPersistenceReader reader = Reader(10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => reader.TryLoadStateRlp(default, ReadFlags.None), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.TryLoadStorageRlp(Keccak.Zero, default, ReadFlags.None), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.GetAccountRaw(default), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => { UInt256 raw = default; reader.TryGetStorageRaw(default, default, ref raw); }, Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.CreateAccountIterator(default, default), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.CreateStorageIterator(default, default, default), Throws.InstanceOf<NotSupportedException>());
            Assert.That(reader.IsPreimageMode, Is.False);
        }
    }

    [Test]
    public void Constructor_ForABlockAboveTheWatermark_ThrowsMissingTrieNode_AndReleasesTheScope()
    {
        HistoryScopeGate gate = new();

        Assert.That(() => Reader(11, gate), Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateNotRetainedException>());

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }

    [Test]
    public void Dispose_Twice_DoesNotReleaseAnotherOpenScope()
    {
        HistoryScopeGate gate = new();
        HistoryBackedPersistenceReader first = Reader(10, gate);
        using HistoryBackedPersistenceReader second = Reader(10, gate);

        first.Dispose();
        first.Dispose();

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None), Is.False,
            "the second reader is still open, so a repeated dispose of the first must not count it closed");
    }

    [Test]
    public void Constructor_ForANonCanonicalStateRoot_ThrowsMissingTrieNode_AndReleasesTheScope()
    {
        HistoryScopeGate gate = new();

        Assert.That(() => Reader(10, gate, TestItem.KeccakA), Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateNotRetainedException>());

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }

    [Test]
    public void GetAccount_OnNormalReader_AllocationIsUnaffectedByConfiguredScopes()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> historyColumns = new();
        HistoryColumnsWriter.RecordAccountV3(historyColumns, Address, block: 5, new Account(5, 500));
        for (ulong block = 0; block <= 10; block++)
        {
            HistoryColumnsWriter.MarkBlockV3(historyColumns, block, Keccak.EmptyTreeHash);
        }
        HistoryColumnsWriter.SetWatermarkV3(historyColumns, 10);

        FlatDbConfig config = new() { HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 2 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(historyColumns, config);
        HistoryReader historyReader = new(db, historyColumns, availability, rowFormat, LimboLogs.Instance);

        HistoryBackedPersistenceReader baselineReader = new(historyReader, new StateId(10, Keccak.EmptyTreeHash), new HistoryScopeGate());
        long baselineAllocated = MeasureAllocatedBytes(() => baselineReader.GetAccount(Address));

        for (int i = 0; i < 200; i++)
        {
            byte[] unrelatedKey = new byte[HistoryKeyLayout.ScopeKeyLength];
            BitConverter.TryWriteBytes(unrelatedKey, i);
            availability.PublishScope(unrelatedKey, floor: 0);
        }

        HistoryBackedPersistenceReader readerWithScopesConfigured = new(historyReader, new StateId(10, Keccak.EmptyTreeHash), new HistoryScopeGate());
        long allocatedWithScopesConfigured = MeasureAllocatedBytes(() => readerWithScopesConfigured.GetAccount(Address));

        Assert.That(allocatedWithScopesConfigured, Is.EqualTo(baselineAllocated),
            "a Normal-mode reader must never consult the scope list at all - its per-call allocation must be identical whether zero or many scopes are configured");
    }

    [Test]
    public void GetAccount_ForAContract_KeepsStorageRootAndCodeHash()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, ContractAddress, 5, new Account(1, 100, TestItem.KeccakA, TestItem.KeccakB));

        Account? contract = Reader(10).GetAccount(ContractAddress);

        Assert.That(contract, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(contract!.StorageRoot, Is.EqualTo(TestItem.KeccakA));
            Assert.That(contract.CodeHash, Is.EqualTo(TestItem.KeccakB));
        }
    }

    [Test]
    public void GetAccount_ForAnAccountWithoutStorageOrCode_AllocatesLessThanAContract()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, ContractAddress, 5, new Account(1, 100, TestItem.KeccakA, TestItem.KeccakB));
        // A reader per block over one warmed history reader, so every measured read misses the read cache and decodes its row.
        HistoryReader history = WarmedHistoryReader();
        HistoryBackedPersistenceReader[] eoaReaders = ReadersFrom(history, 5);
        HistoryBackedPersistenceReader[] contractReaders = ReadersFrom(history, 5);
        int eoaRead = 0;
        int contractRead = 0;

        long eoaAllocated = MeasureAllocatedBytes(() => eoaReaders[eoaRead++].GetAccount(Address));
        long contractAllocated = MeasureAllocatedBytes(() => contractReaders[contractRead++].GetAccount(ContractAddress));
        Dispose(eoaReaders);
        Dispose(contractReaders);

        Assert.That(eoaAllocated, Is.LessThan(contractAllocated),
            "an account without storage or code takes the shared empty-hash instances; only a contract materializes its two hashes");
    }

    [Test]
    public void GetAccount_ReadAgainAtTheSameBlock_ReturnsTheCachedAccountWithoutAllocating()
    {
        using HistoryBackedPersistenceReader reader = Reader(10);
        Account? first = reader.GetAccount(Address);

        long allocated = MeasureAllocatedBytes(() => reader.GetAccount(Address));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetAccount(Address), Is.SameAs(first));
            Assert.That(allocated, Is.Zero);
        }
    }

    [Test]
    public void StorageClearsScopeCache_BeforeAnyStorageRead_AllocatesNoMemo()
    {
        long allocated = MeasureAllocatedBytes(static () => _ = new StorageClearsScopeCache());

        Assert.That(allocated, Is.LessThan(MaxEmptyClearsCacheBytes),
            "a scope that reads no storage must not build the per-account clears memos");
    }

    private const int MeasureRuns = 6;

    /// <remarks>
    /// Takes the minimum over several calls: one call can also pay one-off runtime costs
    /// (lazy thread-local or static initialization) that do not belong to the code under test.
    /// </remarks>
    private static long MeasureAllocatedBytes(Action action)
    {
        action();
        long min = long.MaxValue;
        for (int i = 1; i < MeasureRuns; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return min;
    }

    private HistoryBackedPersistenceReader Reader(ulong block) => Reader(block, new HistoryScopeGate());

    // One reader per MeasureAllocatedBytes run.
    private static HistoryBackedPersistenceReader[] ReadersFrom(HistoryReader history, ulong firstBlock)
    {
        HistoryBackedPersistenceReader[] readers = new HistoryBackedPersistenceReader[MeasureRuns];
        for (int i = 0; i < readers.Length; i++)
            readers[i] = new HistoryBackedPersistenceReader(history, new StateId(firstBlock + (ulong)i, Keccak.EmptyTreeHash), new HistoryScopeGate());
        return readers;
    }

    private static void Dispose(HistoryBackedPersistenceReader[] readers)
    {
        foreach (HistoryBackedPersistenceReader reader in readers) reader.Dispose();
    }

    // The read cache is created on the first stored answer, so a read at an unrelated block keeps that out of a measurement.
    private HistoryReader WarmedHistoryReader()
    {
        HistoryReader history = CreateHistoryReader();
        history.GetAccount(1, ContractAddress, null);
        return history;
    }

    private HistoryReader CreateHistoryReader()
    {
        FlatDbConfig config = new();
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        return new HistoryReader(_db, _historyColumns, availability, rowFormat, LimboLogs.Instance);
    }

    private HistoryBackedPersistenceReader Reader(ulong block, HistoryScopeGate scopeGate) => Reader(block, scopeGate, Keccak.EmptyTreeHash);

    private HistoryBackedPersistenceReader Reader(ulong block, HistoryScopeGate scopeGate, Hash256 stateRoot) =>
        new(CreateHistoryReader(), new StateId(block, stateRoot), scopeGate);
}

public class RestrictedModeHistoryBackedPersistenceReaderTests
{
    private static readonly Address SlicedAddress = new("0x0000000000000000000000000000000000000abc");
    private static readonly Address NonSlicedAddress = new("0x0000000000000000000000000000000000000def");
    private static readonly UInt256 Slot = 7;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

        HistoryColumnsWriter.RecordAccountV3(_historyColumns, SlicedAddress, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, NonSlicedAddress, 5, new Account(9, 900));
        HistoryColumnsWriter.RecordStorageV3(_historyColumns, SlicedAddress, Slot, 5, [0xAA]);

        for (ulong block = 0; block <= 10; block++)
        {
            HistoryColumnsWriter.MarkBlockV3(_historyColumns, block, Keccak.EmptyTreeHash);
        }
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 10);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 8);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public void GetAccount_ForAnAddressCoveredByASliceScope_Resolves()
    {
        Account? account = Reader(3).GetAccount(SlicedAddress);
        Assert.That(account, Is.Not.Null);
        Assert.That(account!.Balance, Is.EqualTo((UInt256)500));
    }

    [Test]
    public void GetAccount_ForAnAddressNotCoveredByAnySliceScope_ThrowsMissingTrieNode() =>
        Assert.That(() => Reader(3).GetAccount(NonSlicedAddress),
            Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateNotRetainedException>());

    [Test]
    public void GetAccount_ForAnAddressWhoseSliceFloorIsDeeperThanTheQueriedBlock_ThrowsMissingTrieNode() =>
        Assert.That(() => Reader(3, sliceFloor: 4).GetAccount(SlicedAddress),
            Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateNotRetainedException>(),
            "the scope's own floor (4) is still above the query (3) - the address is retained only from block 4 onward");

    [Test]
    public void TryGetSlot_ForAnAddressCoveredByASliceScope_Resolves()
    {
        UInt256 value = default;
        bool found = Reader(3).TryGetSlot(SlicedAddress, Slot, ref value);

        Assert.That(found, Is.True);
        Assert.That(value.ToMinimalBigEndian(), Is.EqualTo(new byte[] { 0xAA }));
    }

    [Test]
    public void TryGetSlot_ForAnAddressNotCoveredByAnySliceScope_ThrowsMissingTrieNode() =>
        Assert.That(() =>
        {
            UInt256 value = default;
            Reader(3).TryGetSlot(NonSlicedAddress, Slot, ref value);
        }, Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateNotRetainedException>());

    [Test]
    public void Constructor_ForANonCanonicalStateRoot_ThrowsMissingTrieNode_AndReleasesTheScope_EvenBelowTheFloor()
    {
        HistoryScopeGate gate = new();

        Assert.That(() => Reader(3, sliceFloor: 0, gate, TestItem.KeccakA),
            Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateNotRetainedException>());

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }

    private HistoryBackedPersistenceReader Reader(ulong block, ulong sliceFloor = 0, HistoryScopeGate? scopeGate = null, Hash256? stateRoot = null)
    {
        FlatDbConfig config = new() { HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 2 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        availability.PublishScope(HistoryColumnsWriter.ScopeKeyOf(SlicedAddress), sliceFloor);
        HistoryReader reader = new(_db, _historyColumns, availability, rowFormat, LimboLogs.Instance);
        return new HistoryBackedPersistenceReader(reader, new StateId(block, stateRoot ?? Keccak.EmptyTreeHash), scopeGate ?? new HistoryScopeGate(), restrictToSlices: true);
    }
}
