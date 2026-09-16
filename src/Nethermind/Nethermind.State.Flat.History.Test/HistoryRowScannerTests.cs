// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Walk;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class HistoryRowScannerTests
{
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
}
