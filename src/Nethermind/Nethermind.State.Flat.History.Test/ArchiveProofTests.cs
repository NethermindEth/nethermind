// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Walk;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ArchiveProofTests
{
    private const int AccountCount = 120;
    private const ulong Blocks = 140;

    private static readonly Address Contract = TestItem.AddressA;
    private static readonly Address Absent = TestItem.AddressF;
    private static readonly UInt256[] ContractSlots = [1, 2, 300, 40000];

    private SnapshotableMemColumnsDb<FlatDbColumns> _flatDb = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private ArchiveProofTestChain _chain = null!;
    private Address[] _accounts = null!;

    [SetUp]
    public void SetUp()
    {
        _flatDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _chain = new ArchiveProofTestChain(_historyColumns);
        _accounts = BuildAddresses(AccountCount);
        _policy = TestPolicy;
        _recentEpochs = 0;
        _fineEpochs = 0;
        BuildChain();
    }

    [TearDown]
    public void TearDown()
    {
        _chain.Dispose();
        _flatDb.Dispose();
        _historyColumns.Dispose();
    }

    [TestCase(1ul, TestName = "FirstBlock")]
    [TestCase(7ul, TestName = "MidChain")]
    [TestCase(64ul, TestName = "OnACheckpointBoundary")]
    [TestCase(65ul, TestName = "InsideAnOpenWindow")]
    [TestCase(Blocks, TestName = "Head")]
    public void A_proof_built_from_commitments_equals_the_proof_that_blocks_own_trie_gives(ulong block)
    {
        BuildCommitments();

        foreach (Address address in new[] { _accounts[0], _accounts[AccountCount / 2], _accounts[^1], Contract })
        {
            AssertProofMatchesTheTrie(address, block);
        }
    }

    [TestCase(1ul, 1L, TestName = "StreamedBuild_FirstBlock")]
    [TestCase(65ul, 1L, TestName = "StreamedBuild_InsideAnOpenWindow")]
    [TestCase(Blocks, 1L, TestName = "StreamedBuild_Head")]
    [TestCase(1ul, 40L, TestName = "SplitBuild_FirstBlock")]
    [TestCase(65ul, 40L, TestName = "SplitBuild_InsideAnOpenWindow")]
    [TestCase(Blocks, 40L, TestName = "SplitBuild_Head")]
    public void A_build_under_a_row_budget_that_splits_subtrees_or_streams_keys_yields_the_same_proofs(ulong block, long maxRowsPerPartition)
    {
        BuildCommitments(maxRowsPerPartition);

        foreach (Address address in new[] { _accounts[0], _accounts[AccountCount / 2], _accounts[^1], Contract })
        {
            AssertProofMatchesTheTrie(address, block, address == Contract ? ContractSlots : []);
        }

        CorruptEveryAccountRow();
        AccountProof fromCommitments = ProveFromArchive(_accounts[3], block: 6);
        Assert.That(fromCommitments.Proof!.Select(static item => item.ToHexString()),
            Is.EqualTo(_chain.ExpectedProof(_accounts[3], 6).Proof!.Select(static item => item.ToHexString())),
            "rows combined upward from single-key partitions must leave the same commitment column a whole-subtree replay leaves");
    }

    [Test]
    public void A_build_interrupted_inside_a_subtree_resumes_from_its_last_checkpoint_and_yields_the_same_proofs()
    {
        ArchiveProofRetrofit retrofit = CreateRetrofit(TestPolicy);
        retrofit.Prepare();
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, new FlatDbConfig { HistoryEnabled = true });
        HistoryWalkVerifier verifier = new(_historyColumns, _chain, rowFormat, rlpWrapSlots: true, LimboLogs.Instance, HistoryWalkVerifier.DefaultMaxRowsPerPartition, retrofit);
        CommitmentMetadata metadata = new(_historyColumns, TestPolicy);

        using CancellationTokenSource interrupt = new();
        int checkpoints = 0;
        Assert.That(
            () => verifier.VerifyRangeParallel(0, _chain.Head, workers: 1, checkpointBlocks: 32, (item, block) => { if (item < 256 && ++checkpoints == 1) interrupt.Cancel(); }, interrupt.Token),
            Throws.InstanceOf<OperationCanceledException>(), "precondition: the run is cut right after an account subtree's first checkpoint");

        bool partial = false;
        for (int item = 0; item < 256 && !partial; item++) partial = metadata.TryGetWalkItemProgress(item, out _);
        Assert.That(partial, Is.True, "precondition: at least one subtree left a block-level checkpoint behind");

        HistoryWalkVerdict resumed = verifier.VerifyRangeParallel(0, _chain.Head, workers: 3, CancellationToken.None);
        Assert.That(resumed.Mismatches, Is.Empty, "the resumed run fast-forwards to the checkpoint without rehashing and finishes the range");
        retrofit.PublishCoverage(0, _chain.Head);

        foreach (ulong block in (ulong[])[1, 40, 64, 100, Blocks])
        {
            AssertProofMatchesTheTrie(_accounts[0], block);
            AssertProofMatchesTheTrie(Contract, block, ContractSlots);
        }
    }

    [Test]
    public void A_verify_only_run_interrupted_inside_a_subtree_resumes_from_its_checkpoint_without_false_mismatches()
    {
        HistoryWalkVerifier verifier = CreateVerifyOnlyVerifier();
        CommitmentMetadata metadata = new(_historyColumns, TestPolicy);

        using CancellationTokenSource interrupt = new();
        Assert.That(
            () => verifier.VerifyRangeParallel(0, _chain.Head, workers: 1, checkpointBlocks: 32, (item, block) => { if (item < 256) interrupt.Cancel(); }, interrupt.Token),
            Throws.InstanceOf<OperationCanceledException>(), "precondition: the run is cut right after an account subtree's first checkpoint");
        bool partial = false;
        for (int item = 0; item < 256 && !partial; item++) partial = metadata.TryGetWalkItemProgress(item, out _);
        Assert.That(partial, Is.True, "precondition: a subtree left a block-level checkpoint behind");

        HistoryWalkVerdict resumed = verifier.VerifyRangeParallel(0, _chain.Head, workers: 3, CancellationToken.None);

        Assert.That(resumed.Mismatches, Is.Empty,
            "without a build the depth-2 series is scratch; a resume that deleted it would fold an empty series below the checkpoint and report a state root mismatch at every one of those blocks");
    }

    [Test]
    public void Mismatches_of_a_subtree_finished_before_an_interruption_survive_the_restart()
    {
        CorruptEveryStorageRow();
        List<HistoryWalkMismatch> expected = CreateVerifyOnlyVerifier().VerifyRangeParallel(0, _chain.Head, workers: 1, CancellationToken.None).Mismatches.ToList();
        Assert.That(expected, Is.Not.Empty, "precondition: corrupt slot rows rebuild to storage roots the account rows do not claim");

        HistoryWalkVerifier verifier = CreateVerifyOnlyVerifier();
        using CancellationTokenSource interrupt = new();
        int contractItem = ContractStorageItem;
        Assert.That(
            () => verifier.VerifyRangeParallel(0, _chain.Head, workers: 1, AccountSubtreeReplayer.DefaultCheckpointBlocks, onCheckpoint: null, interrupt.Token, item => { if (item == contractItem) interrupt.Cancel(); }),
            Throws.InstanceOf<OperationCanceledException>(), "precondition: the run is cut right after the contract's storage subtree is marked done");

        HistoryWalkVerdict resumed = verifier.VerifyRangeParallel(0, _chain.Head, workers: 3, CancellationToken.None);

        Assert.That(resumed.Mismatches, Is.EquivalentTo(expected),
            "a finished subtree is skipped on resume, so the mismatches it found must be persisted with its done mark or the resumed verdict passes a corrupt archive");
    }

    [Test]
    public void A_scan_derived_mismatch_is_reported_once_however_often_the_partition_resumes()
    {
        Address moved = _accounts[5];
        HistoryColumnsWriter.RecordAccount(_historyColumns, moved, block: 70, new Account(1, 2005 + 5 * 70, Keccak.Compute("moved"), Keccak.OfAnEmptyString));
        List<HistoryWalkMismatch> expected = CreateVerifyOnlyVerifier().VerifyRangeParallel(0, _chain.Head, workers: 1, CancellationToken.None).Mismatches.ToList();
        Assert.That(expected.Count(static m => m.Kind == HistoryWalkMismatchKind.MissingSlotHistory), Is.EqualTo(2), "precondition: the storage root moves out at block 70 and back at the account's next change, both without slot rows");

        HistoryWalkVerifier verifier = CreateVerifyOnlyVerifier();
        int movedItem = Keccak.Compute(moved.Bytes).Bytes[0];
        using CancellationTokenSource first = new();
        Assert.That(
            () => verifier.VerifyRangeParallel(0, _chain.Head, workers: 1, checkpointBlocks: 32, (item, block) => { if (item == movedItem) first.Cancel(); }, first.Token),
            Throws.InstanceOf<OperationCanceledException>(), "precondition: cut at the moved account's partition checkpoint, after the scan already found the move");
        using CancellationTokenSource second = new();
        Assert.That(
            () => verifier.VerifyRangeParallel(0, _chain.Head, workers: 1, checkpointBlocks: 32, (item, block) => { if (item == movedItem) second.Cancel(); }, second.Token),
            Throws.InstanceOf<OperationCanceledException>(), "precondition: cut there a second time, so the persisted findings went through two resumes");

        HistoryWalkVerdict resumed = verifier.VerifyRangeParallel(0, _chain.Head, workers: 3, CancellationToken.None);

        Assert.That(resumed.Mismatches, Is.EquivalentTo(expected),
            "the scan re-derives its findings on every run, so only the replay's findings may ride along with a checkpoint or every restart would add another copy");
    }

    [Test]
    public void A_partition_that_splits_on_the_resumed_run_does_not_duplicate_the_findings_its_checkpoint_carried()
    {
        Address hot = _accounts[5];
        byte partition = Keccak.Compute(hot.Bytes).Bytes[0];
        for (ulong block = 1; block <= Blocks; block++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, hot, block, new Account(block, 7000 + block, block == 20 ? Keccak.Compute("moved") : Keccak.EmptyTreeHash, Keccak.OfAnEmptyString));
        }

        foreach (Address neighbour in AddressesSortingAfter(hot, count: 2))
        {
            for (ulong block = 10; block <= 50; block += 10) HistoryColumnsWriter.RecordAccount(_historyColumns, neighbour, block, new Account(1, block, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString));
        }

        List<HistoryWalkMismatch> expected = CreateVerifyOnlyVerifier().VerifyRangeParallel(0, _chain.Head, workers: 1, CancellationToken.None).Mismatches.ToList();
        Assert.That(expected.Count(static m => m.Kind == HistoryWalkMismatchKind.MissingSlotHistory), Is.EqualTo(2), "precondition: the hot account's root moves out and back without slot rows");

        using CancellationTokenSource interrupt = new();
        Assert.That(
            () => CreateVerifyOnlyVerifier(maxRowsPerPartition: 12).VerifyRangeParallel(0, _chain.Head, workers: 1, checkpointBlocks: 32, (item, block) => { if (item == partition) interrupt.Cancel(); }, interrupt.Token),
            Throws.InstanceOf<OperationCanceledException>(), "precondition: the hot account streams, its neighbours fit, and the partition is cut at its checkpoint with the streamed moves persisted");

        HistoryWalkVerdict resumed = CreateVerifyOnlyVerifier(maxRowsPerPartition: 6).VerifyRangeParallel(0, _chain.Head, workers: 3, CancellationToken.None);

        Assert.That(resumed.Mismatches, Is.EquivalentTo(expected),
            "a smaller budget splits the partition on resume; its children replay the whole range, so what the checkpoint carried must be dropped or it is reported twice");
    }

    [Test]
    public void Mismatches_found_before_a_checkpoint_survive_the_restart()
    {
        CorruptEveryStorageRow();
        List<HistoryWalkMismatch> expected = CreateVerifyOnlyVerifier().VerifyRangeParallel(0, _chain.Head, workers: 1, CancellationToken.None).Mismatches.ToList();

        HistoryWalkVerifier verifier = CreateVerifyOnlyVerifier();
        using CancellationTokenSource interrupt = new();
        int contractItem = ContractStorageItem;
        Assert.That(
            () => verifier.VerifyRangeParallel(0, _chain.Head, workers: 1, checkpointBlocks: 32, (item, progress) => { if (item == contractItem) interrupt.Cancel(); }, interrupt.Token, checkpointGroups: 1),
            Throws.InstanceOf<OperationCanceledException>(), "precondition: the run is cut at the storage range's checkpoint right after the contract's group");
        Assert.That(new CommitmentMetadata(_historyColumns, TestPolicy).TryGetWalkItemProgress(contractItem, out _), Is.True, "precondition: the storage range left a group checkpoint behind");

        HistoryWalkVerdict resumed = verifier.VerifyRangeParallel(0, _chain.Head, workers: 3, CancellationToken.None);

        Assert.That(resumed.Mismatches, Is.EquivalentTo(expected),
            "groups below the checkpoint are not rescanned on resume, so what they found must ride along with the checkpoint");
    }

    [TestCase(1L)]
    [TestCase(40L)]
    public void A_build_leaves_no_scratch_series_behind(long maxRowsPerPartition)
    {
        BuildCommitments(maxRowsPerPartition);

        IDb column = _historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments);
        Assert.That(column.GetAllKeys().Any(static key => key[0] == SeriesKey.ScratchMarker), Is.False,
            "the per-block series that carry subtree roots between partitions and their combine are scratch and must be deleted once consumed");
    }

    [Test]
    public void An_account_created_and_deleted_inside_one_checkpoint_window_is_served_from_commitments_alone()
    {
        Address transient = TestItem.AddressE;
        ulong born = Blocks + 6;
        ulong died = Blocks + 10;
        ulong queried = Blocks + 8;
        for (ulong number = Blocks + 1; number <= Blocks + 20; number++)
        {
            ulong current = number;
            _chain.AddBlock(number, block =>
            {
                block.SetBalance(_accounts[(int)(current % AccountCount)], (UInt256)(9000 + current));
                if (current == born) block.SetBalance(transient, 777);
                if (current == died) block.SetAccount(transient, null);
            });
        }

        _chain.PublishWatermark();
        BuildCommitments();
        AccountProof expected = _chain.ExpectedProof(transient, queried);

        CorruptEveryAccountRow();
        AccountProof actual = ProveFromArchive(transient, queried);

        Assert.That(actual.Proof!.Select(static item => item.ToHexString()),
            Is.EqualTo(expected.Proof!.Select(static item => item.ToHexString())),
            "a child that appeared and vanished inside one window is in neither the anchor's nor the window's end presence, so only its changed bit lets the resolver find it without a rebuild");
    }

    [Test]
    public void A_storage_proof_built_from_commitments_equals_the_proof_that_blocks_own_trie_gives()
    {
        BuildCommitments();

        foreach (ulong block in (ulong[])[3, 9, 64, 100, Blocks])
        {
            AssertProofMatchesTheTrie(Contract, block, ContractSlots);
        }
    }

    [TestCase(1ul)]
    [TestCase(64ul)]
    [TestCase(127ul)]
    [TestCase(128ul)]
    [TestCase(130ul)]
    [TestCase(Blocks)]
    public void Proofs_resolve_across_epoch_buckets(ulong block)
    {
        _policy = EpochPolicy;
        BuildCommitments();

        foreach (Address address in new[] { _accounts[0], _accounts[AccountCount / 2], _accounts[^1], Contract })
        {
            AssertProofMatchesTheTrie(address, block, address == Contract ? ContractSlots : []);
        }
    }

    [Test]
    public void A_node_untouched_in_an_epoch_still_resolves_from_commitments_alone_through_the_epoch_start_snapshot()
    {
        _policy = EpochPolicy;
        BuildCommitments();
        Address quiet = _accounts.First(static a => a != Contract && Keccak.Compute(a.Bytes).Bytes[0] != Keccak.Compute(Contract.Bytes).Bytes[0]);
        AccountProof expected = _chain.ExpectedProof(quiet, 130);

        CorruptEveryAccountRow();

        AccountProof actual = ProveFromArchive(quiet, 130);
        Assert.That(actual.Proof!.Select(static item => item.ToHexString()), Is.EqualTo(expected.Proof!.Select(static item => item.ToHexString())),
            "block 130 sits in the second epoch; every node on the path has a row there, either from a change or from the snapshot written at the epoch's first block, so the corrupt account rows are never read");
    }

    [TestCase(HistoryWalkVerifier.DefaultMaxRowsPerPartition, TestName = "OnePartition")]
    [TestCase(40L, TestName = "SplitPartitions")]
    [TestCase(6L, TestName = "TwiceSplitPartitions")]
    public void An_epoch_boundary_where_a_contract_stood_still_is_not_reported_as_a_missing_account_row(long maxRowsPerPartition)
    {
        _policy = EpochPolicy;
        _chain.Dispose();
        _historyColumns.Dispose();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _chain = new ArchiveProofTestChain(_historyColumns);
        _chain.AddBlock(0, block =>
        {
            for (int i = 0; i < _accounts.Length; i++) block.SetBalance(_accounts[i], (UInt256)(1000 + i));
            for (int slot = 1; slot <= 200; slot++) block.SetStorage(Contract, (UInt256)slot, [0x20, (byte)slot]);
        });

        for (ulong number = 1; number <= Blocks; number++)
        {
            ulong current = number;
            _chain.AddBlock(number, block => block.SetBalance(_accounts[(int)(current % AccountCount)], (UInt256)(3000 + current)));
        }

        _chain.PublishWatermark();

        ArchiveProofRetrofit retrofit = CreateRetrofit(_policy);
        retrofit.Prepare();
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, new FlatDbConfig { HistoryEnabled = true });
        HistoryWalkVerifier verifier = new(_historyColumns, _chain, rowFormat, rlpWrapSlots: true, LimboLogs.Instance, maxRowsPerPartition, retrofit);

        HistoryWalkVerdict verdict = verifier.VerifyRangeParallel(0, _chain.Head, workers: 3, CancellationToken.None);

        Assert.That(verdict.Mismatches, Is.Empty,
            "the contract's storage never moves after genesis, but every epoch start publishes its subtree view so the row is there for a later read; that publish must not read as a storage root change, or the verdict fails on a healthy archive and no coverage is ever published");
    }

    [TestCase(6, TestName = "SixIsTheSmallestIntervalButFarTooSmallAnEpoch")]
    [TestCase(15, TestName = "OneShortOfReaching")]
    public void An_epoch_whose_two_byte_number_cannot_reach_a_plausible_chain_height_is_refused(int epochLog2) =>
        Assert.That(() => CommitmentDepthPolicy.FromConfig(new FlatDbConfig { ArchiveProofEpochLog2 = epochLog2 }), Throws.InstanceOf<InvalidConfigurationException>(),
            "the epoch is a two-byte key prefix, so too small an epoch runs out of numbers partway up the chain and every later row would throw where nothing names the setting");

    [Test]
    public void Pruning_is_refused_when_the_commitments_are_built_from_the_tip_alone()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, ArchiveProofBuildEnabled = true, ArchiveProofRecentEpochs = 1, ArchiveProofFineEpochs = 1 };
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        ArchiveProofSettings settings = new(config, rowFormat, LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.RecentEpochs, Is.Zero, "the tip path writes no epoch-start snapshot, so a node that stood still for an epoch keeps its only row in an older one and dropping that epoch would publish heights it cannot prove");
            Assert.That(settings.FineEpochs, Is.Zero);
        }
    }

    [Test]
    public void A_demoted_epoch_still_proves_every_height_it_covers()
    {
        _policy = EpochPolicy;
        _fineEpochs = 1;
        BuildCommitments();

        CreateRetrofit(_policy).PruneBelow(_chain.Head);

        foreach (ulong block in (ulong[])[1, 64, 100, 127, 135, Blocks])
        {
            AssertProofMatchesTheTrie(_accounts[2], block);
            AssertProofMatchesTheTrie(Contract, block, ContractSlots);
        }
    }

    [Test]
    public void Demotion_drops_the_per_block_rows_and_keeps_the_checkpoint_rows()
    {
        _policy = EpochPolicy;
        _fineEpochs = 1;
        BuildCommitments();

        CreateRetrofit(_policy).PruneBelow(_chain.Head);

        IDb accounts = _historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.GetAllKeys().Any(key => IsEpochTier(key, epoch: 0, CommitmentKeyLayout.FineTier)), Is.False, "the per-block rows of the demoted epoch are gone in one range delete");
            Assert.That(accounts.GetAllKeys().Any(key => IsEpochTier(key, epoch: 0, CommitmentKeyLayout.CoarseTier)), Is.True, "its window rows stay, which is what keeps that range provable");
            Assert.That(accounts.GetAllKeys().Any(key => IsEpochTier(key, epoch: 1, CommitmentKeyLayout.FineTier)), Is.True, "the epoch inside the fine window keeps both");
        }
    }

    [Test]
    public void Epochs_older_than_the_recent_window_are_dropped_and_proofs_below_them_refused()
    {
        _policy = EpochPolicy;
        _recentEpochs = 1;
        BuildCommitments();
        AccountProof expected = _chain.ExpectedProof(_accounts[1], 130);

        CreateRetrofit(_policy).PruneBelow(_chain.Head);

        IDb accounts = _historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments);
        IDb storages = _historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.GetAllKeys().Any(key => IsEpochTier(key, epoch: 0, CommitmentKeyLayout.FineTier) || IsEpochTier(key, epoch: 0, CommitmentKeyLayout.CoarseTier)), Is.False, "every account row of epoch 0 is gone in one range delete per tier");
            Assert.That(storages.GetAllKeys().Any(key => IsEpochTier(key, epoch: 0, CommitmentKeyLayout.FineTier) || IsEpochTier(key, epoch: 0, CommitmentKeyLayout.CoarseTier)), Is.False, "every storage row of epoch 0 is gone");
            Assert.That(CreateSource(_policy).CanServe(_chain.StateIdAt(100)), Is.False, "a block in the dropped epoch is refused, not served from raw history");
            Assert.That(CreateSource(_policy).CanServe(_chain.StateIdAt(130)), Is.True, "the retained epoch stays servable");
        }

        CorruptEveryAccountRow();
        AccountProof actual = ProveFromArchive(_accounts[1], 130);
        Assert.That(actual.Proof!.Select(static item => item.ToHexString()), Is.EqualTo(expected.Proof!.Select(static item => item.ToHexString())),
            "a retained epoch resolves on its own: its snapshot rows stand in for whatever the dropped epoch held, so the corrupt history rows are never read");
    }

    [Test]
    public void A_small_storage_trie_gets_no_storage_rows_under_the_default_policy_and_still_proves()
    {
        _policy = new CommitmentDepthPolicy(intervalLog2: CommitmentDepthPolicy.MinIntervalLog2);
        BuildCommitments();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments).GetAllKeys(), Is.Empty,
                "a trie of a handful of slots never reaches the rows signal depth; its whole rebuild is one range scan, so rows for it would only duplicate the slot history");
            foreach (ulong block in (ulong[])[3, 64, 100, Blocks]) AssertProofMatchesTheTrie(Contract, block, ContractSlots);
        }
    }

    [Test]
    public void A_proof_resolves_from_history_rows_alone_when_no_commitments_were_built()
    {
        AssertProofMatchesTheTrie(_accounts[3], block: 5);
        AssertProofMatchesTheTrie(Contract, block: 5, ContractSlots);
    }

    [Test]
    public void An_account_that_did_not_exist_at_the_queried_block_proves_its_absence()
    {
        BuildCommitments();

        AccountProof proof = ProveFromArchive(Absent, block: 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof.CodeHash, Is.EqualTo(Hash256.Zero), "an absent account is reported by EIP-1186's zero hashes");
            Assert.That(proof.StorageRoot, Is.EqualTo(Hash256.Zero));
            Assert.That(proof.Proof!.Select(static item => item.ToHexString()),
                Is.EqualTo(_chain.ExpectedProof(Absent, 4).Proof!.Select(static item => item.ToHexString())),
                "the absence proof must be the same path the full trie would have walked");
        }
    }

    [Test]
    public void A_covered_height_is_served_within_a_budget_too_small_for_a_root_rebuild()
    {
        BuildCommitments();
        AccountProof expected = _chain.ExpectedProof(Contract, 9, ContractSlots);

        AccountProofCollector collector = new(Contract, ContractSlots);
        CreateSource(TestPolicy, maxScannedRows: 1500).RunTreeVisitor(collector, _chain.StateIdAt(9), visitingOptions: null, diagnostics: null);
        AccountProof actual = collector.BuildResult();

        Assert.That(actual.Proof!.Select(static item => item.ToHexString()),
            Is.EqualTo(expected.Proof!.Select(static item => item.ToHexString())),
            "the root and the top of the path must come from commitment rows; this budget is an order of magnitude below what rebuilding the root from history rows would scan");
    }

    [Test]
    public void A_proof_served_from_commitments_never_touches_the_history_rows()
    {
        BuildCommitments();
        AccountProof expected = _chain.ExpectedProof(_accounts[3], 6);

        CorruptEveryAccountRow();

        AccountProof actual = ProveFromArchive(_accounts[3], block: 6);

        Assert.That(actual.Proof!.Select(static item => item.ToHexString()),
            Is.EqualTo(expected.Proof!.Select(static item => item.ToHexString())),
            "a fully covered height resolves from the commitment chain alone, every node verified against its parent down from the header");
    }

    [TestCase(1L)]
    [TestCase(40L)]
    public void A_storage_proof_at_a_windows_last_change_is_served_from_commitments_alone(long maxRowsPerPartition)
    {
        BuildCommitments(maxRowsPerPartition);
        AccountProof expected = _chain.ExpectedProof(Contract, Blocks, ContractSlots);

        CorruptEveryStorageRow();

        AccountProof actual = ProveFromArchive(Contract, Blocks, ContractSlots);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < ContractSlots.Length; i++)
            {
                Assert.That(actual.StorageProofs![i].Proof!.Select(static item => item.ToHexString()),
                    Is.EqualTo(expected.StorageProofs![i].Proof!.Select(static item => item.ToHexString())),
                    "at the last block a window row describes, every storage node of a small trie materializes from its window row, so the slot rows are never read; a missing or wrong storage row would make the resolver fall back to the now-corrupt slot rows and refuse");
                Assert.That(actual.StorageProofs[i].Value!.Value.ToArray(), Is.EqualTo(expected.StorageProofs![i].Value!.Value.ToArray()));
            }
        }
    }

    [Test]
    public void A_covered_height_is_refused_when_the_budget_cannot_even_read_its_commitment_rows()
    {
        BuildCommitments();

        AccountProofCollector collector = new(_accounts[3], Array.Empty<UInt256>());
        Assert.That(() => CreateSource(TestPolicy, maxScannedRows: 1).RunTreeVisitor(collector, _chain.StateIdAt(9), visitingOptions: null, diagnostics: null),
            Throws.InstanceOf<StateUnavailableException>(),
            "commitment rows are charged against the same budget as history rows, so a budget of one row cannot walk even the root's chain and must fail closed");
    }

    [Test]
    public void A_commitment_row_that_disagrees_with_the_rows_below_it_is_repaired_instead_of_corrupting_the_proof()
    {
        BuildCommitments();
        AccountProof expected = _chain.ExpectedProof(_accounts[3], 6);

        OverwriteEveryCommitmentValue();

        AccountProof actual = ProveFromArchive(_accounts[3], block: 6);

        Assert.That(actual.Proof!.Select(static item => item.ToHexString()),
            Is.EqualTo(expected.Proof!.Select(static item => item.ToHexString())),
            "a node whose commitment does not match what its parent commits to is rebuilt from the history rows");
    }

    [Test]
    public void A_history_row_that_no_longer_reproduces_the_state_root_is_refused_rather_than_proved()
    {
        CorruptEveryAccountRow();

        Assert.That(() => ProveFromArchive(_accounts[3], block: 6),
            Throws.InstanceOf<StateUnavailableException>(),
            "a proof that cannot be anchored to the header's state root must fail closed, never be served");
    }

    [Test]
    public void Commitments_written_under_a_different_layout_are_refused_before_they_are_mixed()
    {
        CreateRetrofit(TestPolicy).Prepare();

        CommitmentDepthPolicy other = new(intervalLog2: CommitmentDepthPolicy.MinIntervalLog2 + 1);

        Assert.That(() => CreateRetrofit(other).Prepare(),
            Throws.InstanceOf<InvalidConfigurationException>(),
            "rows written under two layouts cannot be read together, so the second build must refuse rather than interleave them");
    }

    [Test]
    public void A_layout_change_discards_the_old_columns_and_rebuilds_when_the_operator_asks()
    {
        BuildCommitments();
        CommitmentDepthPolicy other = new(intervalLog2: CommitmentDepthPolicy.MinIntervalLog2 + 1);
        Assert.That(CreateSource(other).CanServe(_chain.StateIdAt(6)), Is.False, "precondition: the old columns are unreadable under the new layout");

        CreateRetrofit(other, discardMismatchedLayout: true).Prepare();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageCommitments).GetAllKeys(), Is.Empty, "every storage row of the old layout is gone");
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments).GetAllKeys().Count(), Is.EqualTo(1), "only the new stamp remains: rows, coverage and walk marks of the old layout are gone");
            Assert.That(CreateSource(other).CanServe(_chain.StateIdAt(6)), Is.False, "nothing is served until the new build publishes");
        }

        _policy = other;
        BuildCommitments();
        AssertProofMatchesTheTrie(_accounts[0], 6);
        AssertProofMatchesTheTrie(Contract, 100, ContractSlots);
    }

    [TestCase(5, TestName = "BelowRange")]
    [TestCase(13, TestName = "AboveRange")]
    public void A_checkpoint_interval_outside_the_supported_range_is_refused(int intervalLog2) =>
        Assert.That(() => new CommitmentDepthPolicy(intervalLog2), Throws.InstanceOf<InvalidConfigurationException>(),
            "an interval that either explodes the disk or makes every proof replay seconds of changes must be rejected at startup, not discovered in production");

    [Test]
    public void Only_the_heights_the_build_published_are_served()
    {
        _chain.PublishWatermark();
        ArchiveProofSource source = CreateSource(TestPolicy);

        Assert.That(source.CanServe(_chain.StateIdAt(6)), Is.False, "nothing is servable before a build publishes its coverage");

        BuildCommitments();
        source = CreateSource(TestPolicy);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.CanServe(_chain.StateIdAt(6)), Is.True, "a height inside the published coverage is servable");
            Assert.That(source.CanServe(new StateId(Blocks + 5, _chain.StateIdAt(Blocks).StateRoot)), Is.False,
                "a height above the coverage is not");
        }
    }

    [Test]
    public void A_windowed_database_never_serves_historical_proofs()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> windowed = new();
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 128, ArchiveProofServeEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(windowed, config);

        ArchiveProofSource source = new(
            _flatDb,
            windowed,
            new HistoryReader(_flatDb, windowed, availability, rowFormat, LimboLogs.Instance),
            rowFormat,
            TestPolicy,
            new CommitmentMetadata(windowed, CommitmentDepthPolicy.Default),
            new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance),
            config,
            LimboLogs.Instance);

        Assert.That(source.Enabled, Is.False,
            "windowed rows are pre-values behind a retention floor, which a proof resolution cannot replay");
    }

    private static bool IsEpochTier(byte[] key, ulong epoch, byte tier) =>
        key.Length > CommitmentKeyLayout.EpochLength + CommitmentKeyLayout.TierLength
        && key[0] == (byte)(epoch >> 8)
        && key[1] == (byte)epoch
        && key[CommitmentKeyLayout.EpochLength] == tier;

    private static int ContractStorageItem => 256 + Keccak.Compute(Contract.Bytes).Bytes[0];

    private HistoryWalkVerifier CreateVerifyOnlyVerifier(long maxRowsPerPartition = HistoryWalkVerifier.DefaultMaxRowsPerPartition)
    {
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, new FlatDbConfig { HistoryEnabled = true });
        return new HistoryWalkVerifier(_historyColumns, _chain, rowFormat, rlpWrapSlots: true, LimboLogs.Instance, maxRowsPerPartition, emitterSource: null);
    }

    private static IEnumerable<Address> AddressesSortingAfter(Address anchor, int count)
    {
        ValueHash256 anchorPath = Keccak.Compute(anchor.Bytes).ValueHash256;
        for (int seed = 0; count > 0; seed++)
        {
            Address candidate = new(Keccak.Compute(BitConverter.GetBytes(seed)).Bytes[12..]);
            ValueHash256 path = Keccak.Compute(candidate.Bytes).ValueHash256;
            if (path.Bytes[0] != anchorPath.Bytes[0] || path.CompareTo(anchorPath) <= 0) continue;

            count--;
            yield return candidate;
        }
    }

    private static CommitmentDepthPolicy EpochPolicy { get; } = new(CommitmentDepthPolicy.MinIntervalLog2, CommitmentDepthPolicy.DefaultAccountExactDepth, CommitmentDepthPolicy.DefaultAccountCheckpointDepth, CommitmentDepthPolicy.DefaultStorageExactDepth, CommitmentDepthPolicy.DefaultStorageCheckpointDepth, CommitmentDepthPolicy.DefaultLargeTrieSignalDepth, storageRowsSignalDepth: 1, CommitmentDepthPolicy.DefaultAccountComposedDepths, epochLog2: CommitmentDepthPolicy.MinIntervalLog2 + 1);

    private int _recentEpochs;
    private int _fineEpochs;

    private static CommitmentDepthPolicy TestPolicy { get; } = new(CommitmentDepthPolicy.MinIntervalLog2, CommitmentDepthPolicy.DefaultAccountExactDepth, CommitmentDepthPolicy.DefaultAccountCheckpointDepth, CommitmentDepthPolicy.DefaultStorageExactDepth, CommitmentDepthPolicy.DefaultStorageCheckpointDepth, CommitmentDepthPolicy.DefaultLargeTrieSignalDepth, storageRowsSignalDepth: 1);

    private CommitmentDepthPolicy _policy = null!;

    private void BuildChain()
    {
        _chain.AddBlock(0, block =>
        {
            for (int i = 0; i < _accounts.Length; i++) block.SetBalance(_accounts[i], (UInt256)(1000 + i));
            foreach (UInt256 slot in ContractSlots) block.SetStorage(Contract, slot, [0x10, (byte)slot.u0]);
        });

        for (ulong number = 1; number <= Blocks; number++)
        {
            ulong current = number;
            _chain.AddBlock(number, block =>
            {
                for (int i = (int)(current % 5); i < _accounts.Length; i += 5)
                {
                    block.SetBalance(_accounts[i], (UInt256)(2000 + (ulong)i * current));
                }

                block.SetStorage(Contract, ContractSlots[current % (ulong)ContractSlots.Length], [(byte)(current + 1), 0x7F]);
            });
        }

        _chain.PublishWatermark();
    }

    private void BuildCommitments(long maxRowsPerPartition = HistoryWalkVerifier.DefaultMaxRowsPerPartition)
    {
        ArchiveProofRetrofit retrofit = CreateRetrofit(_policy);
        retrofit.Prepare();

        (HistoryAvailability _, HistoryRowFormat rowFormat) =
            HistoryColumnsWriter.CreateSharedFormat(_historyColumns, new FlatDbConfig { HistoryEnabled = true });

        HistoryWalkVerifier verifier = new(
            _historyColumns, _chain, rowFormat, rlpWrapSlots: true, LimboLogs.Instance,
            maxRowsPerPartition, retrofit);

        HistoryWalkVerdict verdict = verifier.VerifyRangeParallel(0, _chain.Head, workers: 3, CancellationToken.None);

        Assert.That(verdict.Mismatches, Is.Empty, "the walk that emits the commitments is also what proves them against the headers");
        retrofit.PublishCoverage(0, _chain.Head);
    }

    private ArchiveProofRetrofit CreateRetrofit(CommitmentDepthPolicy policy, bool discardMismatchedLayout = false)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, ArchiveProofBuildEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofDiscardMismatchedLayout = discardMismatchedLayout, ArchiveProofRecentEpochs = _recentEpochs, ArchiveProofFineEpochs = _fineEpochs };
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        return new ArchiveProofRetrofit(_historyColumns, policy, new CommitmentMetadata(_historyColumns, policy), new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance), LimboLogs.Instance);
    }

    private ArchiveProofSource CreateSource(CommitmentDepthPolicy policy, long maxScannedRows = 0)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, ArchiveProofServeEnabled = true, ArchiveProofMaxScannedRows = maxScannedRows };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        return new ArchiveProofSource(
            _flatDb,
            _historyColumns,
            new HistoryReader(_flatDb, _historyColumns, availability, rowFormat, LimboLogs.Instance),
            rowFormat,
            policy,
            new CommitmentMetadata(_historyColumns, policy),
            new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance),
            config,
            LimboLogs.Instance);
    }

    private AccountProof ProveFromArchive(Address address, ulong block, params UInt256[] storageKeys)
    {
        AccountProofCollector collector = new(address, storageKeys);
        CreateSource(_policy).RunTreeVisitor(collector, _chain.StateIdAt(block), visitingOptions: null, diagnostics: null);
        return collector.BuildResult();
    }

    private void AssertProofMatchesTheTrie(Address address, ulong block, params UInt256[] storageKeys)
    {
        AccountProof expected = _chain.ExpectedProof(address, block, storageKeys);
        AccountProof actual = ProveFromArchive(address, block, storageKeys);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Proof!.Select(static item => item.ToHexString()),
                Is.EqualTo(expected.Proof!.Select(static item => item.ToHexString())),
                $"the account path proven for {address} at block {block} must be the one the full trie holds");
            Assert.That(actual.Balance, Is.EqualTo(expected.Balance), "the proven account must be the account of that block");
            Assert.That(actual.Nonce, Is.EqualTo(expected.Nonce));
            Assert.That(actual.StorageRoot, Is.EqualTo(expected.StorageRoot));
            Assert.That(actual.CodeHash, Is.EqualTo(expected.CodeHash));

            for (int i = 0; i < storageKeys.Length; i++)
            {
                Assert.That(actual.StorageProofs![i].Value!.Value.ToArray(), Is.EqualTo(expected.StorageProofs![i].Value!.Value.ToArray()),
                    $"slot {storageKeys[i]} must hold its block-{block} value");
                Assert.That(actual.StorageProofs[i].Proof!.Select(static item => item.ToHexString()),
                    Is.EqualTo(expected.StorageProofs![i].Proof!.Select(static item => item.ToHexString())),
                    $"the storage path proven for slot {storageKeys[i]} at block {block} must be the one the full trie holds");
            }
        }
    }

    private static Address[] BuildAddresses(int count)
    {
        Address[] addresses = new Address[count];
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[Address.Size];
            BitConverter.TryWriteBytes(bytes.AsSpan(), 0x1000_0000 + i);
            addresses[i] = new Address(bytes);
        }

        return addresses;
    }

    private void OverwriteEveryCommitmentValue()
    {
        IDb column = _historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments);
        List<byte[]> keys = [];
        using (ISortedView view = ((ISortedKeyValueStore)column).GetViewBetween(ReadOnlySpan<byte>.Empty, Bytes.FromHexString("0xff".PadRight(130, 'f'))))
        {
            while (view.MoveNext())
            {
                if (view.CurrentKey.Length > 2) keys.Add(view.CurrentKey.ToArray());
            }
        }

        foreach (byte[] key in keys) column.PutSpan(key, [0x01, 0xC0]);
    }

    private void CorruptEveryStorageRow()
    {
        IDb column = _historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory);
        List<byte[]> keys = [];
        using (ISortedView view = ((ISortedKeyValueStore)column).GetViewBetween(ReadOnlySpan<byte>.Empty, Bytes.FromHexString("0xff".PadRight(130, 'f'))))
        {
            while (view.MoveNext()) keys.Add(view.CurrentKey.ToArray());
        }

        foreach (byte[] key in keys) column.PutSpan(key, Nethermind.Serialization.Rlp.Rlp.Encode(new byte[] { 0xEE, 0xEE }).Bytes);
    }

    private void CorruptEveryAccountRow()
    {
        IDb column = _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory);
        List<byte[]> keys = [];
        using (ISortedView view = ((ISortedKeyValueStore)column).GetViewBetween(ReadOnlySpan<byte>.Empty, Bytes.FromHexString("0xff".PadRight(130, 'f'))))
        {
            while (view.MoveNext()) keys.Add(view.CurrentKey.ToArray());
        }

        foreach (byte[] key in keys)
        {
            Account tampered = new(9999, 9999);
            column.PutSpan(key, Nethermind.Serialization.Rlp.AccountDecoder.Slim.EncodeAsBytes(tampered));
        }
    }
}
