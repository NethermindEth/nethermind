// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class BatchReconcilerTests
{
    private const ulong RollupId = SyncSettlementFixture.RollupId;
    private static readonly Address Beneficiary = SyncSettlementFixture.Beneficiary;

    private IBlockTree _chain = null!;
    private ChainEngine _engine = null!;
    private BatchReconciler _reconciler = null!;
    private FollowerHeads _heads = null!;
    private BlockHeader _genesis = null!;

    [SetUp]
    public void SetUp()
    {
        _chain = Build.A.BlockTree().OfChainLength(1).TestObject;
        _genesis = _chain.Genesis!;
        _engine = new ChainEngine(_chain);
        _heads = new FollowerHeads(_engine, _chain, _genesis);
        _reconciler = new BatchReconciler(_chain, Substitute.For<IStateReader>(), new FakeExecutor(), _engine, DerivedChain.SpecProvider, DerivedChain.Context,
            LimboLogs.Instance);
    }

    [Test]
    public async Task Reconcile_FreshBatch_DerivesItsBlocksToTheSettledHash()
    {
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0), Published(TestItem.PrivateKeyB, 0)];
        Hash256 settled = ExpectedHashes(_genesis, published)[^1];

        BlockHeader end = await _reconciler.Reconcile(Batch(published, settled, _genesis.Hash!), _genesis, _heads);

        Assert.That((end.Number, end.Hash), Is.EqualTo((2UL, settled)), "the block L1's commitment names is the new cursor");
        Assert.That(_engine.Inserted, Has.Count.EqualTo(2), "both blocks enter the chain through the engine");
    }

    [Test]
    public async Task Reconcile_BlocksAlreadyDerived_InsertsNothing()
    {
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0), Published(TestItem.PrivateKeyB, 0)];
        ScannedBatch batch = Batch(published, ExpectedHashes(_genesis, published)[^1], _genesis.Hash!);
        await _reconciler.Reconcile(batch, _genesis, _heads);
        _engine.Inserted.Clear();

        BlockHeader end = await _reconciler.Reconcile(batch, _genesis, _heads);

        Assert.That(end.Number, Is.EqualTo(2), "re-deriving a batch is idempotent");
        Assert.That(_engine.Inserted, Is.Empty, "blocks that already match stay as they are");
    }

    /// <summary>
    /// The local chain holds a first block that matches and a second that does not:
    /// 1. the first is kept;
    /// 2. the second and everything after it is replayed from the DA.
    /// </summary>
    [Test]
    public async Task Reconcile_SecondLocalBlockDiffers_ReplaysFromIt()
    {
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0), Published(TestItem.PrivateKeyB, 0)];
        await Derive(_genesis, [published[0], Published(TestItem.PrivateKeyC, 0)]);
        _engine.Inserted.Clear();
        Hash256 settled = ExpectedHashes(_genesis, published)[^1];

        BlockHeader end = await _reconciler.Reconcile(Batch(published, settled, _genesis.Hash!), _genesis, _heads);

        Assert.That(end.Hash, Is.EqualTo(settled), "the replayed block is the one L1 settled");
        Assert.That(_engine.Inserted.Select(static b => b.Number), Is.EqualTo(new ulong[] { 2 }), "only the differing block is replayed");
    }

    [Test]
    public async Task Reconcile_PartialSettlement_EndsAtTheSettledBlock()
    {
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0), Published(TestItem.PrivateKeyB, 0)];
        Hash256 first = ExpectedHashes(_genesis, published)[0];

        BlockHeader end = await _reconciler.Reconcile(Batch(published, first, _genesis.Hash!), _genesis, _heads);

        Assert.That(end.Number, Is.EqualTo(1), "L1 settled the first block only");
        Assert.That(_chain.Head!.Number, Is.EqualTo(2), "the block above it stays as head, derived but not settled");
    }

    [Test]
    public void Reconcile_SettledHashNotDerived_Throws()
    {
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0)];

        EezFollowerException error = Assert.ThrowsAsync<EezFollowerException>(() =>
            _reconciler.Reconcile(Batch(published, Keccak.Compute("another chain"), _genesis.Hash!), _genesis, _heads))!;

        Assert.That(error.Message, Does.Contain("diverges"), "a settlement the local chain cannot reproduce stops the follower");
    }

    [Test]
    public void Reconcile_CursorIsNeitherEntryNorFinalState_Throws()
    {
        ScannedBatch batch = Batch([Published(TestItem.PrivateKeyA, 0)], Keccak.Compute("final"), Keccak.Compute("entry"));

        Assert.ThrowsAsync<EezFollowerException>(() => _reconciler.Reconcile(batch, _genesis, _heads), "a batch that does not start from the local chain is a divergence");
    }

    [Test]
    public async Task Reconcile_ReplacingASafeBlock_RetreatsSafeFirst()
    {
        BlockHeader[] local = await Derive(_genesis, [Published(TestItem.PrivateKeyC, 0), Published(TestItem.PrivateKeyD, 0)]);
        await _heads.AdvanceSafe(local[^1]);
        _engine.Forkchoices.Clear();
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0), Published(TestItem.PrivateKeyB, 0)];
        Hash256 settled = ExpectedHashes(_genesis, published)[^1];

        await _reconciler.Reconcile(Batch(published, settled, _genesis.Hash!), _genesis, _heads);

        Assert.That(_engine.Forkchoices[0], Is.EqualTo((_genesis.Hash!, _genesis.Hash!, _genesis.Hash!)), "safe and the head move to the parent before the first replaced block");
        Assert.That(_heads.Safe.Hash, Is.EqualTo(_genesis.Hash), "safe stays at the parent until the follower settles the batch");
        Assert.That(_heads.Head.Hash, Is.EqualTo(settled), "the head is the last replayed block");
    }

    [Test]
    public async Task Reconcile_ReplayedBatch_MovesTheHeadOnce()
    {
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0), Published(TestItem.PrivateKeyB, 0), Published(TestItem.PrivateKeyC, 0)];
        Hash256 settled = ExpectedHashes(_genesis, published)[^1];

        await _reconciler.Reconcile(Batch(published, settled, _genesis.Hash!), _genesis, _heads);

        Assert.That(_engine.Forkchoices, Is.EqualTo(new[] { (settled, _genesis.Hash!, _genesis.Hash!) }), "one forkchoice per batch, to its last block");
    }

    /// <summary>
    /// A block with the batch's transactions but a header derivation would not build, as a sequencer could publish:
    /// it does not count as derived, so the batch replaces it.
    /// </summary>
    [Test]
    public async Task Reconcile_LocalBlockWithAnotherHeader_IsReplaced()
    {
        DaBlock published = Published(TestItem.PrivateKeyA, 0);
        Block forged = Build.A.Block.WithParent(_genesis).WithBeneficiary(Beneficiary).WithExtraData(published.ExtraData).WithMixHash(Keccak.Compute("randao"))
            .WithTransactions(published.Transactions.Select(SyncSettlementFixture.Decode).ToArray()).TestObject;
        await _engine.Insert(forged);
        await _heads.SetHead(forged.Header);
        Hash256 settled = ExpectedHashes(_genesis, [published])[0];

        BlockHeader end = await _reconciler.Reconcile(Batch([published], settled, _genesis.Hash!), _genesis, _heads);

        Assert.That(end.Hash, Is.EqualTo(settled), "the settled block replaces the forged one");
        Assert.That(_chain.Head!.Hash, Is.EqualTo(settled), "the forged block is no longer canonical");
    }

    /// <summary>
    /// Replayed blocks that start one batch below the cursor, as a competing composer's batch may:
    /// 1. the anchor is found below the cursor;
    /// 2. the batch builds on it.
    /// </summary>
    [Test]
    public async Task Reconcile_EntryStateBelowTheCursor_BuildsOnIt()
    {
        BlockHeader[] local = await Derive(_genesis, [Published(TestItem.PrivateKeyC, 0)]);
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0)];
        Hash256 settled = ExpectedHashes(_genesis, published)[0];

        BlockHeader end = await _reconciler.Reconcile(Batch(published, settled, _genesis.Hash!), local[0], _heads);

        Assert.That((end.Number, end.Hash), Is.EqualTo((1UL, settled)), "the batch replaces the cursor's block from the anchor below it");
    }

    [Test]
    public async Task Reconcile_ReplacingAFinalizedBlock_Throws()
    {
        BlockHeader[] local = await Derive(_genesis, [Published(TestItem.PrivateKeyC, 0)]);
        await _heads.AdvanceSafe(local[0]);
        await _heads.AdvanceFinalized(local[0]);
        DaBlock[] published = [Published(TestItem.PrivateKeyA, 0)];
        ScannedBatch batch = Batch(published, ExpectedHashes(_genesis, published)[^1], _genesis.Hash!);

        Assert.ThrowsAsync<EezFollowerException>(() => _reconciler.Reconcile(batch, _genesis, _heads), "finalized never moves back");
    }

    /// <summary>
    /// A competing batch in the same L1 block committed Sync block 1 with one transaction; ours settled the next step:
    /// 1. our batch resumes at block 1;
    /// 2. block 1 is rewritten with the existing transaction followed by ours.
    /// </summary>
    [Test]
    public async Task Reconcile_ResumedBatch_AppendsToTheExistingSyncBlock()
    {
        DaBlock competing = Published(TestItem.PrivateKeyA, 0);
        BlockHeader[] local = await Derive(_genesis, [competing]);
        DaBlock ours = Published(TestItem.PrivateKeyB, 0);
        DaBlock combined = ours with { Transactions = [.. competing.Transactions, .. ours.Transactions] };
        Hash256 settled = ExpectedHashes(_genesis, [combined])[0];

        BlockHeader end = await _reconciler.Reconcile(Batch([ours], settled, local[0].Hash!, start: 1), local[0], _heads);

        Assert.That((end.Number, end.Hash), Is.EqualTo((1UL, settled)), "the Sync block now carries both batches' transactions");
    }

    private async Task<BlockHeader[]> Derive(BlockHeader parent, DaBlock[] published)
    {
        List<BlockHeader> headers = [];
        foreach (DaBlock block in published)
        {
            Block built = FakeExecutor.Build(parent, block);
            await _engine.Insert(built);
            await _heads.SetHead(built.Header);
            headers.Add(parent = built.Header);
        }

        return [.. headers];
    }

    private static Hash256[] ExpectedHashes(BlockHeader parent, DaBlock[] published)
    {
        Hash256[] hashes = new Hash256[published.Length];
        for (int i = 0; i < published.Length; i++)
        {
            BlockHeader header = FakeExecutor.Build(parent, published[i]).Header;
            hashes[i] = header.Hash!;
            parent = header;
        }

        return hashes;
    }

    private static DaBlock Published(PrivateKey sender, ulong nonce) =>
        new(Beneficiary, [], [SyncSettlementFixture.Encode(Build.A.Transaction.WithNonce(nonce).SignedAndResolved(sender).TestObject)]);

    private static ScannedBatch Batch(DaBlock[] published, Hash256 finalState, Hash256 entryState, int start = 0)
    {
        L1Batch batch = new(76, Keccak.Compute("L1 block"), Keccak.Compute("batch"), 1, true, entryState, [finalState],
            DaPayloadCodec.Encode(RollupId, published, []));
        return new ScannedBatch(batch, new L1Settlement(start, 1, finalState, entryState));
    }

    /// <summary>Builds a derived block deterministically, without executing it.</summary>
    private sealed class FakeExecutor : IDerivedBlockExecutor, IDerivedBlockSession
    {
        public IDerivedBlockSession BeginSession() => this;

        public Block Execute(BlockHeader parent, DerivedBlock derived) =>
            Build(parent, new DaBlock(derived.Beneficiary, derived.ExtraData, derived.Transactions));

        public static Block Build(BlockHeader parent, DaBlock published) =>
            DerivedChain.Child(parent, published.Beneficiary, published.ExtraData, published.Transactions.Select(SyncSettlementFixture.Decode).ToArray());

        public void Dispose()
        {
        }
    }
}
