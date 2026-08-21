// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryReaderTests
{
    private static readonly Address Address = new("0x0000000000000000000000000000000000000abc");
    private static readonly UInt256 Slot = 7;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        FlatDbConfig config = new();
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _reader = new HistoryReader(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [TestCase(3ul, -1)]
    [TestCase(5ul, 5)]
    [TestCase(19ul, 5)]
    [TestCase(20ul, 20)]
    [TestCase(29ul, 20)]
    [TestCase(30ul, -1)]
    [TestCase(35ul, -1)]
    public void Resolves_account_as_of_block(ulong block, long expectedNonce)
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 20, new Account(20, 2000));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 30, account: null);

        bool found = _reader.TryGetAccount(block, Address, out AccountStruct account);

        if (expectedNonce < 0)
        {
            Assert.That(found, Is.False);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(account.Nonce, Is.EqualTo((ulong)expectedNonce));
            Assert.That(account.Balance, Is.EqualTo((UInt256)(expectedNonce * 100)));
        }
    }

    [TestCase(3ul, null)]
    [TestCase(5ul, "aa")]
    [TestCase(19ul, "aa")]
    [TestCase(20ul, "bbcc")]
    [TestCase(29ul, "bbcc")]
    [TestCase(30ul, null)]
    [TestCase(35ul, null)]
    public void Resolves_storage_as_of_block(ulong block, string? expectedHex)
    {
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 5, [0xAA]);
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 20, [0xBB, 0xCC]);
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 30, ReadOnlySpan<byte>.Empty);

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

    [Test]
    public void TryGetAccount_AtBlockAboveGlobalFloor_ResolvesNormally()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 30);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 5);

        bool found = _reader.TryGetAccount(20, Address, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True, "a read at or above the floor must resolve exactly as it would with no window configured");
            Assert.That(account.Nonce, Is.EqualTo(5UL));
        }
    }

    [Test]
    public void IsAvailable_AtBlockBelowGlobalFloor_ReportsFalseWithoutThrowing()
    {
        HistoryColumnsWriter.MarkBlock(_historyColumns, 6, TestItem.KeccakA);
        HistoryColumnsWriter.SetWatermark(_historyColumns, 30);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 10);

        bool available = _reader.IsAvailable(new StateId(6, TestItem.KeccakA));

        Assert.That(available, Is.False, "the scope-entry gate reports unavailable rather than throwing, so the caller can fall through to its own unavailable-state handling");
    }

    [Test]
    public void IsPrunedBelowFloor_DistinguishesPrunedFromNeverCaptured()
    {
        HistoryColumnsWriter.SetWatermark(_historyColumns, 30);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.IsPrunedBelowFloor(6), Is.True, "covered by the watermark but below the floor must report pruned");
            Assert.That(_reader.IsPrunedBelowFloor(40), Is.False, "above the watermark is 'never captured', not 'pruned'");
        }
    }
}
