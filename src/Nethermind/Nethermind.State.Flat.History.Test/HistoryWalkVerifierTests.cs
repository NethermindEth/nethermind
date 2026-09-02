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

    private HistoryWalkVerifier CreateVerifier(FakeHeaders headers, long maxRowsPerPartition = HistoryWalkVerifier.DefaultMaxRowsPerPartition)
    {
        (HistoryAvailability _, HistoryRowFormat rowFormat) =
            HistoryColumnsWriter.CreateSharedFormat(_historyColumns, new FlatDbConfig { HistoryEnabled = true });
        return new HistoryWalkVerifier(_historyColumns, headers, rowFormat, rlpWrapSlots: true, LimboLogs.Instance, maxRowsPerPartition, emitterSource: null);
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
    public void A_subtree_above_the_row_budget_is_split_and_streamed_and_the_range_still_verifies()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        Account a2 = new(3, 300);
        Account b0 = new(5, 500);
        byte[] slotV1 = [0xAB];
        byte[] slotV2 = [0xCD];
        Account c1 = new(1, 50, StorageRootOf((Slot, slotV1)), Keccak.OfAnEmptyString);
        Account c2 = new(2, 50, StorageRootOf((Slot, slotV2)), Keccak.OfAnEmptyString);
        Address addrC = TestItem.AddressC;

        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);
        HistoryColumnsWriter.RecordAccount(_historyColumns, addrC, block: 1, c1);
        HistoryColumnsWriter.RecordStorage(_historyColumns, addrC, Slot, block: 1, slotV1);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 2, a2);
        HistoryColumnsWriter.RecordAccount(_historyColumns, addrC, block: 2, c2);
        HistoryColumnsWriter.RecordStorage(_historyColumns, addrC, Slot, block: 2, slotV2);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0), (AddrB, b0));
        headers.Roots[1] = StateRootOf((AddrA, a1), (AddrB, b0), (addrC, c1));
        headers.Roots[2] = StateRootOf((AddrA, a2), (AddrB, b0), (addrC, c2));
        MarkAll(headers);

        HistoryWalkVerdict split = CreateVerifier(headers, maxRowsPerPartition: 1).VerifyRange(0, 2, CancellationToken.None);
        HistoryWalkVerdict whole = CreateVerifier(headers).VerifyRange(0, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(split.Mismatches, Is.Empty,
                "a budget of one row forces every subtree to split down to single keys and stream them; the combined roots must still match every header");
            Assert.That(split.Verified, Is.True);
            Assert.That(split.BlocksCompared, Is.EqualTo(3UL));
            Assert.That(whole.Verified, Is.True, "the same range under the normal budget must verify identically");
        }
    }

    [Test]
    public void A_verify_only_walk_leaves_no_rows_behind_in_the_commitment_columns()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0));
        headers.Roots[1] = StateRootOf((AddrA, a1));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers, maxRowsPerPartition: 1).VerifyRange(0, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.True);
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments).GetAllKeys().Count(), Is.Zero,
                "the per-block series a verification streams through the commitment column are scratch and must be gone when it finishes");
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments).GetAllKeys().Count(), Is.Zero);
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
    public void A_contract_born_inside_the_range_with_no_slot_history_is_caught_against_the_empty_tree()
    {
        byte[] value = [0xAB];
        Account born = new(1, 50, StorageRootOf((Slot, value)), Keccak.OfAnEmptyString);

        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 1, born);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf();
        headers.Roots[1] = StateRootOf((AddrB, born));

        MarkAll(headers);
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False,
                "an account first seen inside the range has no prior root to compare against, so its baseline must be the empty tree - not a free pass");
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
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRangeParallel(0, 3, workers: 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Mismatches, Is.Empty);
            Assert.That(verdict.Verified, Is.True,
                "workers replay disjoint subtrees whose roots are combined into one root per block, so the worker count must never weaken the proof");
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
        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRangeParallel(0, 3, workers: 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False);
            Assert.That(verdict.Mismatches.Select(m => m.Block), Does.Contain(2UL),
                "the root combined from all subtrees must report the corrupted height even though every other height is clean");
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
            () => new HistoryWalkVerifier(_historyColumns, new FakeHeaders(), rowFormat, rlpWrapSlots: true, LimboLogs.Instance, HistoryWalkVerifier.DefaultMaxRowsPerPartition, emitterSource: null),
            Throws.InstanceOf<InvalidConfigurationException>(),
            "v3 rows are pre-values with no rows at all for unchanged keys - a genesis-anchored forward walk cannot be sound there and must refuse loudly");
    }
}
