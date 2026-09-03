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
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Walk;
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

        public Action? OnFirstRead { get; set; }

        public ValueHash256? TryGetStateRoot(ulong block)
        {
            Action? hook = OnFirstRead;
            OnFirstRead = null;
            hook?.Invoke();
            return Roots.TryGetValue(block, out ValueHash256 root) ? root : null;
        }
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
    public void A_key_above_the_row_budget_is_streamed_and_the_range_still_verifies()
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
                "a budget of one row forces every key with more than one row to be streamed from disk; the combined roots must still match every header");
            Assert.That(split.Verified, Is.True);
            Assert.That(split.BlocksCompared, Is.EqualTo(3UL));
            Assert.That(whole.Verified, Is.True, "the same range under the normal budget must verify identically");
        }
    }

    [Test]
    public void A_partition_holding_more_keys_than_the_row_budget_is_split_into_its_children_and_still_verifies()
    {
        Address[] siblings = AddressesSharingTheirFirstPathByte(3);
        Account[] first = [new Account(1, 10), new Account(1, 20), new Account(1, 30)];
        Account[] later = [new Account(2, 11), new Account(2, 21), new Account(2, 31)];
        for (int i = 0; i < siblings.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, siblings[i], block: 0, first[i]);
            HistoryColumnsWriter.RecordAccount(_historyColumns, siblings[i], block: 2, later[i]);
        }

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((siblings[0], first[0]), (siblings[1], first[1]), (siblings[2], first[2]));
        headers.Roots[1] = headers.Roots[0];
        headers.Roots[2] = StateRootOf((siblings[0], later[0]), (siblings[1], later[1]), (siblings[2], later[2]));
        MarkAll(headers);

        HistoryWalkVerdict split = CreateVerifier(headers, maxRowsPerPartition: 3).VerifyRange(0, 2, CancellationToken.None);
        HistoryWalkVerdict whole = CreateVerifier(headers).VerifyRange(0, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(split.Mismatches, Is.Empty,
                "three keys under one depth-2 prefix with six rows overflow a three-row budget after the second key, so the partition splits into its children and their views are folded back; the fold must reproduce every header");
            Assert.That(split.BlocksCompared, Is.EqualTo(3UL));
            Assert.That(whole.Verified, Is.True);
        }
    }

    [Test]
    public void A_split_partition_still_names_the_exact_misattributed_height()
    {
        Address[] siblings = AddressesSharingTheirFirstPathByte(3);
        Account before = new(1, 100);
        Account after = new(2, 200);
        foreach (Address sibling in siblings) HistoryColumnsWriter.RecordAccount(_historyColumns, sibling, block: 0, before);
        HistoryColumnsWriter.RecordAccount(_historyColumns, siblings[1], block: 1, after);
        foreach (Address sibling in siblings) HistoryColumnsWriter.RecordAccount(_historyColumns, sibling, block: 3, after);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((siblings[0], before), (siblings[1], before), (siblings[2], before));
        headers.Roots[1] = headers.Roots[0];
        headers.Roots[2] = StateRootOf((siblings[0], before), (siblings[1], after), (siblings[2], before));
        headers.Roots[3] = StateRootOf((siblings[0], after), (siblings[1], after), (siblings[2], after));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers, maxRowsPerPartition: 3).VerifyRange(0, 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False);
            Assert.That(verdict.Mismatches.Select(m => (m.Block, m.Kind)), Does.Contain((1UL, HistoryWalkMismatchKind.StateRoot)),
                "a change recorded one block early inside a split partition must surface at that block through the folded root, not be smeared over the range");
        }
    }

    [Test]
    public void A_storage_trie_holding_more_slots_than_the_row_budget_is_split_by_slot_nibble_and_still_verifies()
    {
        UInt256[] slots = [1, 2, 3];
        byte[][] v1 = [[0x01], [0x02], [0x03]];
        byte[][] v2 = [[0x11], [0x12], [0x13]];
        Account b0 = new(1, 50, StorageRootOf((slots[0], v1[0]), (slots[1], v1[1]), (slots[2], v1[2])), Keccak.OfAnEmptyString);
        Account b2 = new(2, 50, StorageRootOf((slots[0], v2[0]), (slots[1], v2[1]), (slots[2], v2[2])), Keccak.OfAnEmptyString);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 2, b2);
        for (int i = 0; i < slots.Length; i++)
        {
            HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, slots[i], block: 0, v1[i]);
            HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, slots[i], block: 2, v2[i]);
        }

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrB, b0));
        headers.Roots[1] = headers.Roots[0];
        headers.Roots[2] = StateRootOf((AddrB, b2));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers, maxRowsPerPartition: 3).VerifyRange(0, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Mismatches, Is.Empty,
                "six slot rows of one contract overflow a three-row budget after the second slot, so the trie is split by slot nibble and folded back per block; the folded storage root must equal the owner's recorded root");
            Assert.That(verdict.BlocksCompared, Is.EqualTo(3UL));
        }
    }

    [Test]
    public void A_corrupted_slot_row_inside_a_split_storage_trie_is_still_caught()
    {
        UInt256[] slots = [1, 2, 3];
        byte[][] v1 = [[0x01], [0x02], [0x03]];
        byte[][] v2 = [[0x11], [0x12], [0x13]];
        Account b0 = new(1, 50, StorageRootOf((slots[0], v1[0]), (slots[1], v1[1]), (slots[2], v1[2])), Keccak.OfAnEmptyString);
        Account b2 = new(2, 50, StorageRootOf((slots[0], v2[0]), (slots[1], v2[1]), (slots[2], v2[2])), Keccak.OfAnEmptyString);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 2, b2);
        for (int i = 0; i < slots.Length; i++)
        {
            HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, slots[i], block: 0, v1[i]);
            HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, slots[i], block: 2, i == 1 ? [0xEE] : v2[i]);
        }

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrB, b0));
        headers.Roots[1] = headers.Roots[0];
        headers.Roots[2] = StateRootOf((AddrB, b2));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers, maxRowsPerPartition: 3).VerifyRange(0, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Mismatches.Select(m => (m.Block, m.Kind)), Is.EquivalentTo(new[] { (2UL, HistoryWalkMismatchKind.StorageRoot) }),
                "the per-contract root check must survive the split: the folded root at block 2 disagrees with the honest account row and nothing else does");
            Assert.That(verdict.BlocksCompared, Is.EqualTo(3UL), "a storage mismatch reports and continues");
        }
    }

    [Test]
    public void A_destruct_inside_a_split_storage_trie_empties_every_sub_partition()
    {
        UInt256[] slots = [1, 2, 3];
        byte[][] v1 = [[0x01], [0x02], [0x03]];
        byte[][] v2 = [[0x11], [0x12], [0x13]];
        Account b0 = new(1, 50, StorageRootOf((slots[0], v1[0]), (slots[1], v1[1]), (slots[2], v1[2])), Keccak.OfAnEmptyString);
        Account b1 = new(2, 50, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        Account b2 = new(3, 50, StorageRootOf((slots[0], v2[0]), (slots[1], v2[1]), (slots[2], v2[2])), Keccak.OfAnEmptyString);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 1, b1);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 2, b2);
        RecordClear(AddrB, block: 1);
        for (int i = 0; i < slots.Length; i++)
        {
            HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, slots[i], block: 0, v1[i]);
            HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, slots[i], block: 2, v2[i]);
        }

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrB, b0));
        headers.Roots[1] = StateRootOf((AddrB, b1));
        headers.Roots[2] = StateRootOf((AddrB, b2));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers, maxRowsPerPartition: 3).VerifyRange(0, 2, CancellationToken.None);

        Assert.That(verdict.Mismatches, Is.Empty,
            "a clear inside the range must reset every slot-nibble sub-partition at its block, so the folded root is the empty tree there and the rebuilt values afterwards");
    }

    [Test]
    public void A_slot_row_at_a_block_the_account_column_never_recorded_is_caught()
    {
        Account b0 = new(1, 50, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, Slot, block: 1, [0xAB]);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrB, b0));
        headers.Roots[1] = headers.Roots[0];
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 1, CancellationToken.None);

        Assert.That(verdict.Mismatches.Select(m => (m.Block, m.Kind)), Is.EquivalentTo(new[] { (1UL, HistoryWalkMismatchKind.MissingAccountRow) }),
            "every storage change moves the owner's storage root, so a slot row at a block with no account row for its owner cannot have produced the header honestly");
    }

    [Test]
    public void A_storage_root_that_moves_at_a_block_with_no_slot_row_is_caught_for_a_contract_that_has_slot_history()
    {
        byte[] v1 = [0xAB];
        byte[] v2 = [0xCD];
        Account b0 = new(1, 50, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        Account b1 = new(2, 50, StorageRootOf((Slot, v1)), Keccak.OfAnEmptyString);
        Account b2 = new(3, 50, StorageRootOf((Slot, v2)), Keccak.OfAnEmptyString);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 1, b1);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 2, b2);
        HistoryColumnsWriter.RecordStorage(_historyColumns, AddrB, Slot, block: 1, v1);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrB, b0));
        headers.Roots[1] = StateRootOf((AddrB, b1));
        headers.Roots[2] = StateRootOf((AddrB, b2));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 2, CancellationToken.None);

        Assert.That(verdict.Mismatches.Select(m => (m.Block, m.Kind)), Is.EquivalentTo(new[] { (2UL, HistoryWalkMismatchKind.MissingSlotHistory) }),
            "the contract has slot history, so the merge of its rebuilt roots with its account rows must see the root move at block 2 with no slot change there");
    }

    [Test]
    public void A_streamed_accounts_storage_root_move_without_slot_rows_is_caught()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        Account a2 = new(3, 300, StorageRootOf((Slot, [0xAB])), Keccak.OfAnEmptyString);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 2, a2);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0));
        headers.Roots[1] = StateRootOf((AddrA, a1));
        headers.Roots[2] = StateRootOf((AddrA, a2));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers, maxRowsPerPartition: 1).VerifyRange(0, 2, CancellationToken.None);

        Assert.That(verdict.Mismatches.Select(m => (m.Block, m.Kind)), Is.EquivalentTo(new[] { (2UL, HistoryWalkMismatchKind.MissingSlotHistory) }),
            "a streamed key is skipped by the scanner, so only the replayer sees its rows; it must still notice the storage root moving with no slot history behind it");
    }

    [Test]
    public void A_clear_row_alone_does_not_excuse_a_storage_root_that_moved_without_slot_rows()
    {
        Account b0 = new(1, 50, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        Account b1 = new(2, 50, StorageRootOf((Slot, [0xAB])), Keccak.OfAnEmptyString);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 1, b1);
        RecordClear(AddrB, block: 1);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrB, b0));
        headers.Roots[1] = StateRootOf((AddrB, b1));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 1, CancellationToken.None);

        Assert.That(verdict.Mismatches.Select(m => (m.Block, m.Kind)), Is.EquivalentTo(new[] { (1UL, HistoryWalkMismatchKind.MissingSlotHistory) }),
            "a contract with a clear but no slot rows is never visited by the storage side, so the account side must keep checking it; a clear cannot explain a root that moved to a non-empty trie");
    }

    private static Address[] AddressesSharingTheirFirstPathByte(int count)
    {
        Dictionary<byte, List<Address>> groups = [];
        for (int i = 1; ; i++)
        {
            byte[] bytes = new byte[Address.Size];
            BitConverter.TryWriteBytes(bytes.AsSpan(), 0x7000_0000 + i);
            Address address = new(bytes);
            byte first = address.ToAccountPath.Bytes[0];
            if (!groups.TryGetValue(first, out List<Address>? group))
            {
                group = [];
                groups[first] = group;
            }

            group.Add(address);
            if (group.Count == count) return [.. group];
        }
    }

    [Test]
    public void A_walk_interrupted_before_the_root_fold_resumes_without_starting_over()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        Account b0 = new(5, 500);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrB, block: 0, b0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0), (AddrB, b0));
        headers.Roots[1] = StateRootOf((AddrA, a1), (AddrB, b0));
        MarkAll(headers);

        using CancellationTokenSource interrupt = new();
        headers.OnFirstRead = interrupt.Cancel;
        CommitmentMetadata metadata = new(_historyColumns);

        Assert.That(() => CreateVerifier(headers).VerifyRange(0, 1, interrupt.Token), Throws.InstanceOf<OperationCanceledException>(),
            "precondition: the run is cut at the root fold, after every subtree finished");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.TryGetWalkInProgress(out ulong from, out ulong to) && from == 0 && to == 1, Is.True,
                "an interrupted run leaves its range behind so the next start knows what to resume");
            Assert.That(metadata.IsWalkItemDone(0) && metadata.IsWalkItemDone(HistoryWalkRun.WorkItems - 1), Is.True,
                "every subtree that finished is marked, so a restart does not replay it");
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments).GetAllKeys().Any(static key => key[0] == SeriesKey.ScratchMarker), Is.True,
                "the finished subtrees' series stay on disk for the fold that never ran");
        }

        HistoryWalkVerdict resumed = CreateVerifier(headers).VerifyRange(0, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.Verified, Is.True, "the resumed run folds the preserved series and matches every header");
            Assert.That(resumed.BlocksCompared, Is.EqualTo(2UL));
            Assert.That(metadata.TryGetWalkInProgress(out _, out _), Is.False, "a finished run leaves no resume state");
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments).GetAllKeys().Any(static key => key[0] == SeriesKey.ScratchMarker), Is.False);
        }
    }

    [Test]
    public void Resume_state_for_a_different_range_is_discarded_and_the_walk_starts_over()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0));
        headers.Roots[1] = StateRootOf((AddrA, a1));
        MarkAll(headers);

        CommitmentMetadata metadata = new(_historyColumns);
        metadata.BeginWalk(0, 7, HistoryWalkRun.WorkItems);
        for (int item = 0; item < HistoryWalkRun.WorkItems; item++) metadata.MarkWalkItemDone(item, []);

        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(0, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.True,
                "marks left by a run over another range describe series this run never wrote, so they must be thrown away rather than trusted");
            Assert.That(metadata.TryGetWalkInProgress(out _, out _), Is.False);
        }
    }

    [Test]
    public void A_single_block_range_compares_that_block_alone_and_returns()
    {
        Account a0 = new(1, 100);
        Account a1 = new(2, 200);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 0, a0);
        HistoryColumnsWriter.RecordAccount(_historyColumns, AddrA, block: 1, a1);

        FakeHeaders headers = new();
        headers.Roots[0] = StateRootOf((AddrA, a0));
        headers.Roots[1] = StateRootOf((AddrA, a1));
        MarkAll(headers);

        HistoryWalkVerdict verdict = CreateVerifier(headers).VerifyRange(1, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.True, "a range of one block anchors at that block and compares it; nothing lies above it to scan for");
            Assert.That(verdict.BlocksCompared, Is.EqualTo(1UL));
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
