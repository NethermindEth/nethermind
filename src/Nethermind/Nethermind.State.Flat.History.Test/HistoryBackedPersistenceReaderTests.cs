// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
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

    private HistoryBackedPersistenceReader Reader(ulong block) =>
        new(new HistoryReader(_db, _historyColumns, LimboLogs.Instance), new StateId(block, Keccak.EmptyTreeHash), new HistoryScopeGate());
}
