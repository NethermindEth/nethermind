// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class TransactionChangesetStoreTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _store = new TransactionChangesetStore(_columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets));
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void AnAccountEntry_RoundTrips()
    {
        byte[] value = [0x01, 0x02, 0x03];
        byte[] buffer = new byte[ChangesetCodec.MaxAccountEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, TestItem.AddressA, value);

        List<(byte Kind, byte[] Address, byte[] Index, byte[] Value)> read = Decode(buffer.AsSpan(0, length));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read, Has.Count.EqualTo(1));
            Assert.That(read[0].Kind, Is.EqualTo(ChangesetCodec.AccountKind));
            Assert.That(read[0].Address, Is.EqualTo(TestItem.AddressA.Bytes.ToArray()));
            Assert.That(read[0].Value, Is.EqualTo(value));
        }
    }

    [TestCase(0, 0, TestName = "ClearedSlotOfAZeroIndex")]
    [TestCase(1, 32, TestName = "ShortIndexFullValue")]
    [TestCase(32, 1, TestName = "FullIndexShortValue")]
    public void AStorageEntry_RoundTrips(int indexLength, int valueLength)
    {
        byte[] index = Filled(indexLength, 0xA0);
        byte[] value = Filled(valueLength, 0xB0);
        byte[] buffer = new byte[ChangesetCodec.MaxStorageEntryLength];
        int length = ChangesetCodec.WriteStorage(buffer, TestItem.AddressB, index, value);

        List<(byte Kind, byte[] Address, byte[] Index, byte[] Value)> read = Decode(buffer.AsSpan(0, length));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read[0].Kind, Is.EqualTo(ChangesetCodec.StorageKind));
            Assert.That(read[0].Address, Is.EqualTo(TestItem.AddressB.Bytes.ToArray()));
            Assert.That(read[0].Index, Is.EqualTo(index));
            Assert.That(read[0].Value, Is.EqualTo(value));
        }
    }

    [Test]
    public void MixedEntries_KeepTheirOrder()
    {
        byte[] buffer = new byte[3 * ChangesetCodec.MaxStorageEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, TestItem.AddressA, [0x11]);
        length += ChangesetCodec.WriteStorage(buffer.AsSpan(length), TestItem.AddressB, [0x01], [0x22]);
        length += ChangesetCodec.WriteAccount(buffer.AsSpan(length), TestItem.AddressC, []);

        List<(byte Kind, byte[] Address, byte[] Index, byte[] Value)> read = Decode(buffer.AsSpan(0, length));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read, Has.Count.EqualTo(3), "a reader walks the entries in the order the transaction wrote them");
            Assert.That(read[2].Address, Is.EqualTo(TestItem.AddressC.Bytes.ToArray()));
            Assert.That(read[2].Value, Is.Empty, "an account with no value is how the row records a deletion");
        }
    }

    [Test]
    public void ATruncatedRow_IsRefused()
    {
        byte[] buffer = new byte[ChangesetCodec.MaxAccountEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, TestItem.AddressA, [0x11, 0x22]);

        Assert.That(() => Decode(buffer.AsSpan(0, length - 1)), Throws.InstanceOf<InvalidDataException>(),
            "a row read back short is corruption, not an empty changeset: it must not decode to a shorter but plausible write list");
    }

    [Test]
    public void TheWritesBeforeATransaction_AreOneRangeInExecutionOrder()
    {
        WriteTransaction(block: 10, transactionIndex: 0, TestItem.AddressA);
        WriteTransaction(block: 10, transactionIndex: 1, TestItem.AddressB);
        WriteTransaction(block: 10, transactionIndex: 2, TestItem.AddressC);
        WriteTransaction(block: 11, transactionIndex: 0, TestItem.AddressD);

        List<(ulong Block, ushort Index)> seen = [];
        using (ISortedView view = _store.OpenBefore(block: 10, beforeTransaction: 2))
        {
            while (view.MoveNext()) seen.Add((ChangesetKeyLayout.BlockOf(view.CurrentKey), ChangesetKeyLayout.TransactionIndexOf(view.CurrentKey)));
        }

        Assert.That(seen, Is.EqualTo(new[] { (10UL, (ushort)0), (10UL, (ushort)1) }),
            "the scan must stop before the traced transaction and must not cross into the next block");
    }

    [Test]
    public void Coverage_ExtendsOnlyWhenTheNewRangeTouchesIt()
    {
        bool first = _store.TryExtendCoverage(100, 200);
        bool adjacent = _store.TryExtendCoverage(201, 300);
        bool disjoint = _store.TryExtendCoverage(500, 600);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.True);
            Assert.That(adjacent, Is.True);
            Assert.That(disjoint, Is.False, "coverage is one contiguous range; claiming a disjoint one would report a gap as indexed");
            Assert.That(_store.TryGetCoverage(out ulong from, out ulong to) && from == 100 && to == 300, Is.True);
            Assert.That(_store.Covers(250), Is.True);
            Assert.That(_store.Covers(550), Is.False);
        }
    }

    [Test]
    public void CoverageIsAbsent_OnAFreshColumn() =>
        Assert.That(_store.TryGetCoverage(out _, out _), Is.False);

    private void WriteTransaction(ulong block, ushort transactionIndex, Address address)
    {
        byte[] buffer = new byte[ChangesetCodec.MaxAccountEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, address, [0x01]);
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columns.StartWriteBatch();
        _store.Write(block, transactionIndex, buffer.AsSpan(0, length), batch.GetColumnBatch(FlatHistoryColumns.TransactionChangesets));
    }

    private static byte[] Filled(int length, byte first)
    {
        byte[] bytes = new byte[length];
        if (length > 0) bytes[0] = first;
        return bytes;
    }

    private static List<(byte Kind, byte[] Address, byte[] Index, byte[] Value)> Decode(ReadOnlySpan<byte> changeset)
    {
        List<(byte, byte[], byte[], byte[])> entries = [];
        ChangesetCodec.Enumerator enumerator = ChangesetCodec.Read(changeset);
        while (enumerator.MoveNext())
        {
            entries.Add((enumerator.Kind, enumerator.Address.ToArray(), enumerator.Index.ToArray(), enumerator.Value.ToArray()));
        }

        return entries;
    }
}
