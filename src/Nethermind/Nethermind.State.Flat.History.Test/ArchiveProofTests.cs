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
            new CommitmentMetadata(windowed),
            new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance),
            config,
            LimboLogs.Instance);

        Assert.That(source.Enabled, Is.False,
            "windowed rows are pre-values behind a retention floor, which a proof resolution cannot replay");
    }

    private static CommitmentDepthPolicy TestPolicy { get; } = new(intervalLog2: CommitmentDepthPolicy.MinIntervalLog2);

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
        ArchiveProofRetrofit retrofit = CreateRetrofit(TestPolicy);
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

    private ArchiveProofRetrofit CreateRetrofit(CommitmentDepthPolicy policy)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, ArchiveProofBuildEnabled = true, HistoryVerifyEveryBlock = true };
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        return new ArchiveProofRetrofit(_historyColumns, policy, new CommitmentMetadata(_historyColumns), new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance), LimboLogs.Instance);
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
            new CommitmentMetadata(_historyColumns),
            new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance),
            config,
            LimboLogs.Instance);
    }

    private AccountProof ProveFromArchive(Address address, ulong block, params UInt256[] storageKeys)
    {
        AccountProofCollector collector = new(address, storageKeys);
        CreateSource(TestPolicy).RunTreeVisitor(collector, _chain.StateIdAt(block), visitingOptions: null, diagnostics: null);
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
