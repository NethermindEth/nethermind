// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class HistoryReaderTests
{
    private static readonly Address Address = new("0x0000000000000000000000000000000000000abc");
    private static readonly UInt256 Slot = 7;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private HistoryReader _reader = null!;
    private HistoryStore _accountStore = null!;
    private HistoryStore _storageStore = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _reader = new HistoryReader(_db, rlpWrapSlots: true);
        _accountStore = new HistoryStore(
            _db.GetColumnDb(FlatDbColumns.AccountHistory),
            _db.GetColumnDb(FlatDbColumns.AccountChangeSets));
        _storageStore = new HistoryStore(
            _db.GetColumnDb(FlatDbColumns.StorageHistory),
            _db.GetColumnDb(FlatDbColumns.StorageChangeSets));
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    // Account: nonce/balance set at block 5, overwritten at 20, deleted at 30. -1 == absent.
    [TestCase(3, -1)]
    [TestCase(5, 5)]
    [TestCase(19, 5)]
    [TestCase(20, 20)]
    [TestCase(29, 20)]
    [TestCase(30, -1)]
    [TestCase(35, -1)]
    public void Resolves_account_as_of_block(long block, long expectedNonce)
    {
        RecordAccount(5, new Account(5, 500));
        RecordAccount(20, new Account(20, 2000));
        RecordAccount(30, account: null);

        bool found = _reader.TryGetAccount(block, Address, out AccountStruct account);

        if (expectedNonce < 0)
        {
            Assert.That(found, Is.False);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(account.Nonce, Is.EqualTo((UInt256)expectedNonce));
            Assert.That(account.Balance, Is.EqualTo((UInt256)(expectedNonce * 100)));
        }
    }

    // Storage: 0xAA at block 5, 0xBBCC at block 20, cleared at block 30. null == unset.
    [TestCase(3, null)]
    [TestCase(5, "aa")]
    [TestCase(19, "aa")]
    [TestCase(20, "bbcc")]
    [TestCase(29, "bbcc")]
    [TestCase(30, null)]
    [TestCase(35, null)]
    public void Resolves_storage_as_of_block(long block, string? expectedHex)
    {
        RecordStorage(5, [0xAA]);
        RecordStorage(20, [0xBB, 0xCC]);
        RecordStorage(30, ReadOnlySpan<byte>.Empty);

        bool found = _reader.TryGetStorage(block, Address, Slot, out SlotValue value);

        if (expectedHex is null)
        {
            Assert.That(found, Is.False);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(value.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(Convert.FromHexString(expectedHex)));
        }
    }

    private void RecordAccount(long block, Account? account)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], Address.ToAccountPath);

        using IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        IWriteBatch history = batch.GetColumnBatch(FlatDbColumns.AccountHistory);
        IWriteBatch changeMarkers = batch.GetColumnBatch(FlatDbColumns.AccountChangeSets);

        if (account is null)
        {
            _accountStore.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty, history, changeMarkers);
            return;
        }

        byte[] buffer = new byte[256];
        RlpStream rlp = new(buffer);
        AccountDecoder.Slim.Encode(account, rlp);
        _accountStore.RecordChange(block, flatKey, buffer.AsSpan(0, rlp.Position), history, changeMarkers);
    }

    private void RecordStorage(long block, ReadOnlySpan<byte> rawValue)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(Slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], Address.ToAccountPath, slotHash);

        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = rawValue.IsEmpty
            ? 0
            : BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(rawValue), rlpWrapSlots: true, value);

        using IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();
        _storageStore.RecordChange(
            block, flatKey, value[..written],
            batch.GetColumnBatch(FlatDbColumns.StorageHistory),
            batch.GetColumnBatch(FlatDbColumns.StorageChangeSets));
    }
}
