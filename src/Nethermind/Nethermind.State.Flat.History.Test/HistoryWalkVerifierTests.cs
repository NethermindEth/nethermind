// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryWalkVerifierTests
{
    private static readonly Address AddrA = TestItem.AddressA;
    private static readonly Address AddrB = TestItem.AddressB;
    private static readonly UInt256 Slot = 1;

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp() => _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

    [TearDown]
    public void TearDown() => _historyColumns.Dispose();

    private HistoryWalkVerifier CreateVerifier(FakeHeaders headers, long maxMaterializedRows = HistoryWalkVerifier.DefaultMaxMaterializedRows)
    {
        (HistoryAvailability _, HistoryRowFormat rowFormat) =
            HistoryColumnsWriter.CreateSharedFormat(_historyColumns, new FlatDbConfig { HistoryEnabled = true });
        return new HistoryWalkVerifier(_historyColumns, headers, rowFormat, rlpWrapSlots: true, LimboLogs.Instance, maxMaterializedRows);
    }

    private sealed class FakeHeaders : IHistoryHeaderSource
    {
        public Dictionary<ulong, ValueHash256> Roots { get; } = [];

        public ValueHash256? TryGetStateRoot(ulong block) => Roots.TryGetValue(block, out ValueHash256 root) ? root : null;
    }

    private static Hash256 StorageRootOf(params (UInt256 Slot, byte[] Value)[] slots)
    {
        StorageTree tree = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((UInt256 slot, byte[] value) in slots)
        {
            tree.Set(slot, value);
        }

        tree.UpdateRootHash();
        return tree.RootHash;
    }

    private static ValueHash256 StateRootOf(params (Address Address, Account Account)[] accounts)
    {
        StateTree tree = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Address address, Account account) in accounts)
        {
            tree.Set(address, account);
        }

        tree.UpdateRootHash();
        return new ValueHash256(tree.RootHash.Bytes);
    }

    private void RecordClear(Address address, ulong block)
    {
        StorageClearStore clears = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageClears));
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        Span<byte> key = stackalloc byte[HistoryKeyLayout.AccountKeyLength];
        clears.RecordClear(block, address.ToAccountPath.Bytes, batch.GetColumnBatch(FlatHistoryColumns.StorageClears));
    }

    private void MarkAll(FakeHeaders headers)
    {
        foreach ((ulong block, ValueHash256 root) in headers.Roots)
        {
            HistoryColumnsWriter.MarkBlock(_historyColumns, block, root);
        }
    }

    [Test]
    public void Walk_over_a_clean_history_matches_every_header_including_a_destruct()
    {
        Account a0 = new(1, 100);
        Account a2 = new(2, 200);
        byte[] slotV1 = [0xAB];
        byte[] slotV2 = [0xCD];
        Account b0 = new(1, 50, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        Account b1 = new(2, 50, StorageRootOf((Slot, slotV1)), Keccak.OfAnEmptyString);
        Account b2 = new(3, 50, StorageRootOf((Slot, slotV2)), Keccak.OfAnEmptyString);

        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 1, b1);
        HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, Slot, block: 1, slotV1);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 2, a2);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 2, b2);
        HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, Slot, block: 2, slotV2);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 3, null);
        RecordClear(AddrB, block: 3);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0), (AddrB, b0));
        headers.Roots[1] = StateRootOf((AddrA, a0), (AddrB, b1));
        headers.Roots[2] = StateRootOf((AddrA, a2), (AddrB, b2));
        headers.Roots[3] = StateRootOf((AddrA, a2));

        MarkAll(headers);
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Mismatches, Is.Empty,
                "an honest history must reproduce the header root at every block, through storage changes and a self-destruct alike");
            Assert.That(verdict.Verified, Is.True);
            Assert.That(verdict.BlocksCompared, Is.EqualTo(4UL), "every block in the range must actually be compared, not sampled");
        }
    }

    [Test]
    public void A_range_needing_more_rows_than_the_ceiling_is_declined_before_the_partition_allocates_them()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        Account a2 = new(3, 300);

        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 2, a2);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0));
        headers.Roots[1] = StateRootOf((AddrA, a1));
        headers.Roots[2] = StateRootOf((AddrA, a2));
        MarkAll(headers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => CreateVerifier(headers, maxMaterializedRows: 1).VerifyRange(0, 2, CancellationToken.None),
                Throws.InstanceOf<InvalidConfigurationException>(),
                "a request whose working set exceeds the ceiling must be declined rather than allowed to exhaust the node");
            Assert.That(CreateVerifier(headers).VerifyRange(0, 2, CancellationToken.None).Verified, Is.True,
                "the same range under the normal ceiling must still verify");
        }
    }

    [Test]
    public void A_change_attributed_to_the_wrong_block_is_caught_at_the_misattributed_height()
    {
        Account before = new(1, 100);
        Account after = new(2, 200);

        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, before);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, after);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, before));
        headers.Roots[1] = StateRootOf((AddrA, before));
        headers.Roots[2] = StateRootOf((AddrA, after));

        MarkAll(headers);
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False,
                "a tip-only or sampled check passes this history - the final state is correct - which is exactly why every block must be compared");
            Assert.That(verdict.Mismatches, Has.Count.EqualTo(1));
            Assert.That(verdict.Mismatches[0].Kind, Is.EqualTo(HistoryWalkMismatchKind.StateRoot));
            Assert.That(verdict.Mismatches[0].Block, Is.EqualTo(1UL), "the walk names the exact height whose as-of answers would be wrong");
        }
    }

    [Test]
    public void A_corrupted_slot_row_is_caught_against_the_account_records_own_storage_root()
    {
        byte[] honest = [0xAB];
        byte[] corrupted = [0xEE];
        Account b0 = new(1, 50, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        Account b1 = new(2, 50, StorageRootOf((Slot, honest)), Keccak.OfAnEmptyString);

        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 1, b1);
        HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, Slot, block: 1, corrupted);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrB, b0));
        headers.Roots[1] = StateRootOf((AddrB, b1));

        MarkAll(headers);
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False,
                "the state root check alone passes here - the account record carries the honest storageRoot - so only the storage rebuild catches a corrupted slot row");
            Assert.That(verdict.Mismatches.Select(m => m.Kind), Is.EquivalentTo(new[] { HistoryWalkMismatchKind.StorageRoot }));
            Assert.That(verdict.Mismatches[0].Block, Is.EqualTo(1UL));
            Assert.That(verdict.BlocksCompared, Is.EqualTo(2UL), "a storage mismatch reports and continues - it does not derail the state walk");
        }
    }

    [Test]
    public void A_missing_slot_row_is_caught_when_the_account_records_storage_root_moves_without_slot_history()
    {
        byte[] value = [0xAB];
        Account b0 = new(1, 50, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        Account b1 = new(2, 50, StorageRootOf((Slot, value)), Keccak.OfAnEmptyString);

        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 1, b1);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrB, b0));
        headers.Roots[1] = StateRootOf((AddrB, b1));

        MarkAll(headers);
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False,
                "the state root check alone passes here - the account row carries the root the header expects - so only the moved-root-without-slot-history check catches a deleted slot row");
            Assert.That(verdict.Mismatches.Select(m => m.Kind), Is.EquivalentTo(new[] { HistoryWalkMismatchKind.MissingSlotHistory }));
            Assert.That(verdict.Mismatches[0].Block, Is.EqualTo(1UL));
        }
    }

    [Test]
    public void Parallel_segments_verify_the_same_history_each_anchored_at_its_own_start()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        Account a2 = new(3, 300);
        Account a3 = new(4, 400);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 2, a2);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 3, a3);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0));
        headers.Roots[1] = StateRootOf((AddrA, a1));
        headers.Roots[2] = StateRootOf((AddrA, a2));
        headers.Roots[3] = StateRootOf((AddrA, a3));

        MarkAll(headers);
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRangeParallel(0, 3, segments: 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Mismatches, Is.Empty);
            Assert.That(verdict.Verified, Is.True,
                "each segment builds its own start state from the rows and anchors it to its own start header, so splitting must never weaken the proof");
            Assert.That(verdict.BlocksCompared, Is.GreaterThanOrEqualTo(4UL));
        }
    }

    [Test]
    public void Parallel_segments_still_name_the_exact_corrupted_height()
    {
        Account before = new(1, 100);
        Account after = new(2, 200);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, before);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 2, after);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, before));
        headers.Roots[1] = StateRootOf((AddrA, before));
        headers.Roots[2] = StateRootOf((AddrA, before));
        headers.Roots[3] = StateRootOf((AddrA, before));

        MarkAll(headers);
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRangeParallel(0, 3, segments: 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False);
            Assert.That(verdict.Mismatches.Select(m => m.Block), Does.Contain(2UL),
                "the segment covering the corrupted height must report it even though the other segments are clean");
        }
    }

    [Test]
    public void A_corrupted_captured_marker_over_honest_rows_is_caught()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0));
        headers.Roots[1] = StateRootOf((AddrA, a1));

        HistoryColumnsWriter.MarkBlock(_historyColumns, 0, headers.Roots[0]);
        HistoryColumnsWriter.MarkBlock(_historyColumns, 1, TestItem.KeccakF);

        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False,
                "the serving gate trusts the captured marker, not the rebuilt root - a corrupt marker over honest rows misroutes EIP-1898 requests and only the per-block marker check sees it");
            Assert.That(verdict.Mismatches.Select(m => m.Kind), Is.EquivalentTo(new[] { HistoryWalkMismatchKind.CapturedMarker }));
            Assert.That(verdict.Mismatches[0].Block, Is.EqualTo(1UL));
            Assert.That(verdict.BlocksCompared, Is.EqualTo(2UL), "a marker mismatch reports and continues - the state walk itself is unaffected");
        }
    }

    [Test]
    public void A_windowed_database_is_refused()
    {
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(
            _historyColumns, new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 100 });

        Assert.That(
            () => new HistoryWalkVerifier(_historyColumns, new FakeHeaders(), rowFormat, rlpWrapSlots: true, LimboLogs.Instance),
            Throws.InstanceOf<InvalidConfigurationException>(),
            "v3 rows are pre-values with no rows at all for unchanged keys - a genesis-anchored forward walk cannot be sound there and must refuse loudly");
    }
}
