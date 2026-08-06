// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryBackedPersistenceReaderTests
{
    private static readonly Address Address = new("0x0000000000000000000000000000000000000abc");
    private static readonly UInt256 Slot = 7;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 5, [0xAA]);

        // The constructor re-validates availability against the SAME state root every Reader(block) call below
        // uses (Keccak.EmptyTreeHash) - closing the check-register race means a block must genuinely be available
        // (covered by the watermark, root matching) before a reader can be built for it at all, matching the real
        // HistoricalFlatDbManager -> HistoryBackedPersistenceReader call chain.
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
        SlotValue present = default;
        SlotValue absent = default;
        bool foundPresent = Reader(10).TryGetSlot(Address, Slot, ref present);
        bool foundAbsent = Reader(3).TryGetSlot(Address, Slot, ref absent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundPresent, Is.True);
            Assert.That(present.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0xAA }));
            Assert.That(foundAbsent, Is.False);
        }
    }

    // Corrupt value rows are decoded here, after the bundle gather, so FlatStateReader's gather-level translation
    // never sees them; the reader itself must map them to the unavailable-state contract (MissingTrieNodeException),
    // which JSON-RPC reports as resource-not-found instead of an internal error.
    [Test]
    public void Corrupt_account_row_surfaces_as_missing_trie_node()
    {
        HistoryColumnsWriter.RecordRawAccountRow(_historyColumns, Address, 6, new byte[300]);

        Assert.That(() => Reader(10).GetAccount(Address),
            Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateUnavailableException>());
    }

    [Test]
    public void Corrupt_storage_row_surfaces_as_missing_trie_node()
    {
        HistoryColumnsWriter.RecordRawStorageRow(_historyColumns, Address, Slot, 6, new byte[300]);

        Assert.That(() =>
        {
            SlotValue value = default;
            Reader(10).TryGetSlot(Address, Slot, ref value);
        }, Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateUnavailableException>());
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
            Assert.That(() => { SlotValue raw = default; reader.TryGetStorageRaw(default, default, ref raw); }, Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.CreateAccountIterator(default, default), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.CreateStorageIterator(default, default, default), Throws.InstanceOf<NotSupportedException>());
            Assert.That(reader.IsPreimageMode, Is.False);
        }
    }

    // Closes the check-register race between HistoricalFlatDbManager's own (pre-construction) availability check
    // and this reader's scope registration: the constructor re-validates under its own scope, so a block that is
    // not actually available (here: above the watermark) must fail closed at construction, not be silently served.
    [Test]
    public void Constructor_ForABlockAboveTheWatermark_ThrowsMissingTrieNode_AndReleasesTheScope()
    {
        HistoryScopeGate gate = new();

        Assert.That(() => Reader(11, gate), Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateUnavailableException>());

        // No leaked scope: draining succeeds immediately, proving the failed construction released what it took.
        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }

    // EIP-1898: a non-canonical root at an otherwise-covered height must also fail closed at construction, not
    // just at a later read - the same re-validation this constructor performs after entering its own scope.
    [Test]
    public void Constructor_ForANonCanonicalStateRoot_ThrowsMissingTrieNode_AndReleasesTheScope()
    {
        HistoryScopeGate gate = new();

        Assert.That(() => Reader(10, gate, TestItem.KeccakA), Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateUnavailableException>());

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

        FlatDbConfig config = new() { HistoryRetentionBlocks = 2 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(historyColumns, config);
        HistoryReader historyReader = new(db, historyColumns, config, availability, rowFormat, LimboLogs.Instance);

        HistoryBackedPersistenceReader baselineReader = new(historyReader, new StateId(10, Keccak.EmptyTreeHash), new HistoryScopeGate());
        long baselineAllocated = MeasureAllocatedBytes(() => baselineReader.GetAccount(Address));

        for (int i = 0; i < 200; i++)
        {
            byte[] unrelatedKey = new byte[BaseFlatPersistence.AccountKeyLength];
            BitConverter.TryWriteBytes(unrelatedKey, i);
            availability.PublishScope(unrelatedKey, floor: 0);
        }

        HistoryBackedPersistenceReader readerWithScopesConfigured = new(historyReader, new StateId(10, Keccak.EmptyTreeHash), new HistoryScopeGate());
        long allocatedWithScopesConfigured = MeasureAllocatedBytes(() => readerWithScopesConfigured.GetAccount(Address));

        Assert.That(allocatedWithScopesConfigured, Is.EqualTo(baselineAllocated),
            "a Normal-mode reader must never consult the scope list at all - its per-call allocation must be identical whether zero or many scopes are configured");
    }

    private static long MeasureAllocatedBytes(Action action)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private HistoryBackedPersistenceReader Reader(ulong block) => Reader(block, new HistoryScopeGate());

    private HistoryBackedPersistenceReader Reader(ulong block, HistoryScopeGate scopeGate) => Reader(block, scopeGate, Keccak.EmptyTreeHash);

    private HistoryBackedPersistenceReader Reader(ulong block, HistoryScopeGate scopeGate, Hash256 stateRoot)
    {
        FlatDbConfig config = new();
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        HistoryReader reader = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
        return new HistoryBackedPersistenceReader(reader, new StateId(block, stateRoot), scopeGate);
    }
}

public class RestrictedHistoryBackedPersistenceReaderTests
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
        Account? account = Reader(3, SliceScopes()).GetAccount(SlicedAddress);
        Assert.That(account, Is.Not.Null);
        Assert.That(account!.Balance, Is.EqualTo((UInt256)500));
    }

    [Test]
    public void GetAccount_ForAnAddressNotCoveredByAnySliceScope_ThrowsMissingTrieNode() =>
        Assert.That(() => Reader(3, SliceScopes()).GetAccount(NonSlicedAddress),
            Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateUnavailableException>());

    [Test]
    public void GetAccount_ForAnAddressWhoseSliceFloorIsDeeperThanTheQueriedBlock_ThrowsMissingTrieNode()
    {
        IReadOnlyList<ScopeFloor> scopesWithShallowerFloor = [new ScopeFloor(AccountKeyOf(SlicedAddress), Floor: 4, IsGeneral: false)];
        Assert.That(() => Reader(3, scopesWithShallowerFloor).GetAccount(SlicedAddress),
            Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateUnavailableException>(),
            "the scope's own floor (4) is still above the query (3) - the address is retained only from block 4 onward");
    }

    [Test]
    public void TryGetSlot_ForAnAddressCoveredByASliceScope_Resolves()
    {
        SlotValue value = default;
        bool found = Reader(3, SliceScopes()).TryGetSlot(SlicedAddress, Slot, ref value);

        Assert.That(found, Is.True);
        Assert.That(value.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0xAA }));
    }

    [Test]
    public void TryGetSlot_ForAnAddressNotCoveredByAnySliceScope_ThrowsMissingTrieNode() =>
        Assert.That(() =>
        {
            SlotValue value = default;
            Reader(3, SliceScopes()).TryGetSlot(NonSlicedAddress, Slot, ref value);
        }, Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateUnavailableException>());

    [Test]
    public void Constructor_ForANonCanonicalStateRoot_ThrowsMissingTrieNode_AndReleasesTheScope_EvenBelowTheFloor()
    {
        HistoryScopeGate gate = new();

        Assert.That(() => Reader(3, SliceScopes(), gate, TestItem.KeccakA),
            Throws.InstanceOf<MissingTrieNodeException>().With.InnerException.InstanceOf<StateUnavailableException>());

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }

    [Test]
    public void Dispose_ReleasesTheScope()
    {
        HistoryScopeGate gate = new();
        RestrictedHistoryBackedPersistenceReader reader = Reader(3, SliceScopes(), gate, Keccak.EmptyTreeHash);

        reader.Dispose();

        Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.FromSeconds(5), CancellationToken.None), Is.True);
    }

    [Test]
    public void Unsupported_members_throw_not_supported()
    {
        RestrictedHistoryBackedPersistenceReader reader = Reader(3, SliceScopes());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => reader.TryLoadStateRlp(default, ReadFlags.None), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.TryLoadStorageRlp(Keccak.Zero, default, ReadFlags.None), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.GetAccountRaw(default), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => { SlotValue raw = default; reader.TryGetStorageRaw(default, default, ref raw); }, Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.CreateAccountIterator(default, default), Throws.InstanceOf<NotSupportedException>());
            Assert.That(() => reader.CreateStorageIterator(default, default, default), Throws.InstanceOf<NotSupportedException>());
            Assert.That(reader.IsPreimageMode, Is.False);
        }
    }

    private static IReadOnlyList<ScopeFloor> SliceScopes() => [new ScopeFloor(AccountKeyOf(SlicedAddress), Floor: 0, IsGeneral: false)];

    private static byte[] AccountKeyOf(Address address)
    {
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        return BaseFlatPersistence.EncodeAccountKeyHashed(buffer, address.ToAccountPath).ToArray();
    }

    private RestrictedHistoryBackedPersistenceReader Reader(ulong block, IReadOnlyList<ScopeFloor> sliceScopes) =>
        Reader(block, sliceScopes, new HistoryScopeGate(), Keccak.EmptyTreeHash);

    private RestrictedHistoryBackedPersistenceReader Reader(ulong block, IReadOnlyList<ScopeFloor> sliceScopes, HistoryScopeGate scopeGate, Hash256 stateRoot)
    {
        FlatDbConfig config = new() { HistoryRetentionBlocks = 2 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        HistoryReader reader = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
        return new RestrictedHistoryBackedPersistenceReader(reader, new StateId(block, stateRoot), scopeGate, sliceScopes);
    }
}
