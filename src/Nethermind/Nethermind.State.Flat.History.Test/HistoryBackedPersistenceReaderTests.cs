// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryBackedPersistenceReaderTests
{
    private static readonly Address Address = new("0x0000000000000000000000000000000000000abc");
    private static readonly UInt256 Slot = 7;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryStore _accountStore = null!;
    private HistoryStore _storageStore = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _accountStore = new HistoryStore(
            _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory),
            _historyColumns.GetColumnDb(FlatHistoryColumns.AccountChangeSets));
        _storageStore = new HistoryStore(
            _historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory),
            _historyColumns.GetColumnDb(FlatHistoryColumns.StorageChangeSets));

        RecordAccount(5, new Account(5, 500));
        RecordStorage(5, [0xAA]);
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

    [Test]
    public void Pins_current_state_to_its_block() =>
        Assert.That(Reader(10).CurrentState.BlockNumber, Is.EqualTo(10));

    // Flat history has no trie nodes / raw-import data / iteration, so a historical trie traversal (eth_getProof,
    // verifyTrie) must fail loudly as unsupported rather than silently return a wrong proof or an empty state walk.
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
        new(new HistoryReader(_db, _historyColumns, LimboLogs.Instance), new StateId(block, Keccak.EmptyTreeHash));

    private void RecordAccount(ulong block, Account account)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], Address.ToAccountPath);

        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        _accountStore.RecordChange(
            block, flatKey, rlp,
            batch.GetColumnBatch(FlatHistoryColumns.AccountHistory),
            batch.GetColumnBatch(FlatHistoryColumns.AccountChangeSets));
    }

    private void RecordStorage(ulong block, ReadOnlySpan<byte> rawValue)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(Slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], Address.ToAccountPath, slotHash);

        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(rawValue), rlpWrapSlots: true, value);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        _storageStore.RecordChange(
            block, flatKey, value[..written],
            batch.GetColumnBatch(FlatHistoryColumns.StorageHistory),
            batch.GetColumnBatch(FlatHistoryColumns.StorageChangeSets));
    }
}
