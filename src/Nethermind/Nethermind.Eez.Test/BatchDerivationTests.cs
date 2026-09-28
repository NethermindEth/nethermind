// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class BatchDerivationTests
{
    private const string Window = "captured-devnet-window-438";
    private const int From = 433;
    private const int To = 438;

    private static readonly Lazy<ISpecProvider> Spec = new(static () => StatelessFixtures.ReadSpecProvider(Window, "chain-config.json"));

    private SyncSettlementFixture _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new SyncSettlementFixture();

    /// <summary>
    /// The composer's recorded window, rebuilt from its batch alone:
    /// 1. decode the DA of the <c>postAndVerifyBatch</c> L1 accepted, which settled its anchor and all three effects;
    /// 2. rebuild each block's transactions, the Sync block's system transactions included;
    /// 3. build each header on the previous recorded one and compare every field derivation fixes.
    /// </summary>
    [Test]
    public void Derive_RecordedDevnetWindow_RebuildsEveryBlock()
    {
        (Block[] recorded, DerivedBlock[] derived, EezSettlementContext context) = RecordedWindow();
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(StatelessFixtures.ReadPostBatch(Window));

        Assert.That(derived, Has.Length.EqualTo(recorded.Length), "the DA spans the recorded window");
        BlockHeader parent = WindowParent();
        for (int i = 0; i < recorded.Length; i++)
        {
            Block block = recorded[i];
            Assert.That(derived[i].Transactions, Is.EqualTo(block.Transactions.Select(SyncSettlementFixture.Encode).ToArray()),
                $"block {block.Number} gets the recorded transactions, byte for byte");
            IReleaseSpec spec = Spec.Value.GetSpec(block.Header);
            BlockHeader header = DerivedHeader.Build(parent, spec, context, derived[i].Beneficiary, derived[i].ExtraData);
            Assert.That(Inputs(header), Is.EqualTo(Inputs(block.Header)), $"block {block.Number} gets the recorded header inputs");
            Assert.That(DerivedHeader.Withdrawals(spec), Is.EqualTo(block.Withdrawals), $"block {block.Number} gets the recorded withdrawals");
            parent = block.Header;
        }

        Assert.That(recorded[^1].Transactions.Count(static t => t.Type == EezConstants.SystemTxType), Is.EqualTo(batch.Entries.Length - 1),
            "precondition: the Sync block carries a system transaction per effect");
    }

    /// <summary>
    /// Each block of the recorded window, built from its derived header and transactions on the recorded parent
    /// state, produces the hash the composer settled: roots, gas and bloom come out of execution alone.
    /// </summary>
    [Test]
    public void Build_RecordedDevnetWindow_ReproducesEveryBlockHash()
    {
        (Block[] recorded, DerivedBlock[] derived, EezSettlementContext context) = RecordedWindow();
        EezStatelessExecutor executor = new(Spec.Value, LimboLogs.Instance);
        DerivedBlockBuilder builder = new(Spec.Value, context);

        BlockHeader parent = WindowParent();
        for (int i = 0; i < recorded.Length; i++)
        {
            using Witness witness = StatelessFixtures.ReadWitness(Window, $"witness-{recorded[i].Number}.json");
            StatelessBlockProcessingEnv env = executor.CreateEnvironment(witness);

            (Block built, _) = builder.Build(parent, derived[i], env.BlockProcessor, env.WorldState);

            Assert.That(built.Hash, Is.EqualTo(recorded[i].Hash), $"block {recorded[i].Number} builds to the recorded hash");
            parent = recorded[i].Header;
        }
    }

    [Test]
    public void Build_TransactionInvalidOnTheParentState_Throws()
    {
        (Block[] recorded, DerivedBlock[] derived, EezSettlementContext context) = RecordedWindow();
        using Witness witness = StatelessFixtures.ReadWitness(Window, $"witness-{To}.json");
        StatelessBlockProcessingEnv env = new EezStatelessExecutor(Spec.Value, LimboLogs.Instance).CreateEnvironment(witness);
        byte[] user = derived[^1].Transactions.First(static t => t[0] != (byte)EezConstants.SystemTxType);
        DerivedBlock replayed = derived[^1] with { Transactions = [.. derived[^1].Transactions, user] };

        EezSettlementException error = Assert.Throws<EezSettlementException>(() =>
            new DerivedBlockBuilder(Spec.Value, context).Build(recorded[^2].Header, replayed, env.BlockProcessor, env.WorldState))!;

        Assert.That(error.Message, Does.Contain("does not execute"), "a replayed user transaction fails the block instead of being left out");
    }

    [Test]
    public void Derive_WholeBatchSettled_RebuildsTheSyncBlock()
    {
        DerivedBlock[] derived = Derive(new ProducingSlice(0, 2), _fixture.Payload());

        Assert.That(derived[0].Transactions, Is.EqualTo(new[] { _fixture.PrecedingTransaction }), "a block before the Sync block keeps its published transactions");
        Assert.That(derived[1].Transactions, Is.EqualTo(_fixture.SyncTransactions), "the Sync block is [load, user, delivery]");
        Assert.That(derived[1].ExtraData, Is.EqualTo(new byte[] { 9 }), "the Sync block keeps its published extra data");
    }

    /// <summary>
    /// A competing batch in the same L1 block already applied the outbound effect:
    /// 1. its load and user transaction are not rebuilt, the user transaction is dropped;
    /// 2. the delivery takes the nonce after the skipped load.
    /// </summary>
    [Test]
    public void Derive_OutboundEffectAlreadyApplied_RebuildsOnlyTheDelivery()
    {
        DerivedBlock[] derived = Derive(new ProducingSlice(1, 1), _fixture.Payload());

        Assert.That(derived[1].Transactions, Is.EqualTo(new[] { _fixture.SyncTransactions[2] }), "only the delivery remains, at the nonce after the skipped load");
    }

    [Test]
    public void Derive_OnlyTheOutboundEffectSettled_DropsTheDelivery()
    {
        DerivedBlock[] derived = Derive(new ProducingSlice(0, 1), _fixture.Payload());

        Assert.That(derived[1].Transactions, Is.EqualTo(_fixture.SyncTransactions[..2]), "the inbound effect L1 did not apply gets no delivery");
    }

    [Test]
    public void Derive_TrailingUserTransaction_FollowsTheEffects()
    {
        byte[] trailing = _fixture.PrecedingTransaction;

        DerivedBlock[] derived = Derive(new ProducingSlice(0, 2), _fixture.Payload(settlingPublished: [_fixture.UserTransaction, trailing]));

        Assert.That(derived[1].Transactions, Is.EqualTo(_fixture.SyncTransactions.Append(trailing).ToArray()),
            "a published transaction that pairs with no effect runs after the deliveries");
    }

    [Test]
    public void Derive_FewerUserTransactionsThanOutboundEffects_Throws()
    {
        EezSettlementException error = Assert.Throws<EezSettlementException>(() => Derive(new ProducingSlice(0, 2), _fixture.Payload(settlingPublished: [])))!;

        Assert.That(error.Failure, Is.EqualTo(EezSettlementFailure.InvalidDaPayload), "the DA cannot pair every outbound effect with its user transaction");
    }

    [Test]
    public void Derive_PayloadOfAnotherRollup_Throws()
    {
        DaPayload payload = DaPayloadCodec.Decode(_fixture.Payload());

        Assert.Throws<EezSettlementException>(() =>
            BatchDerivation.Derive(payload, new ProducingSlice(0, 2), SyncSettlementFixture.StartingNonce, SyncSettlementFixture.ChainId, SyncSettlementFixture.RollupId + 1));
    }

    [TestCase(0, 1, 0, 0, TestName = "AnchorOnly")]
    [TestCase(0, 3, 0, 2, TestName = "AnchorAndTwoEffects")]
    [TestCase(1, 2, 0, 2, TestName = "AnchorAlreadyApplied")]
    [TestCase(3, 1, 2, 1, TestName = "LastEffectOnly")]
    public void OfSettledRun_ClaimedSteps_SkipsTheAnchor(int start, int length, int skip, int take) =>
        Assert.That(ProducingSlice.OfSettledRun(start, length), Is.EqualTo(new ProducingSlice(skip, take)), "claimed step i is effect i - 1");

    /// <summary>The recorded blocks and the blocks derivation rebuilds from the batch that settled them, all of it applied.</summary>
    private static (Block[] Recorded, DerivedBlock[] Derived, EezSettlementContext Context) RecordedWindow()
    {
        Block[] recorded = Enumerable.Range(From, To - From + 1)
            .Select(static n => Rlp.Decode<Block>(StatelessFixtures.ReadBlock(Window, $"block-{n}.rlp.hex"))!).ToArray();
        JsonElement oracle = StatelessFixtures.ReadJson(Window, "oracle.json");
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(StatelessFixtures.ReadPostBatch(Window));
        ulong systemNonce = recorded[^1].Transactions.First(static t => t.Type == EezConstants.SystemTxType).Nonce;
        EezSettlementContext context = new(oracle.GetProperty("rollup_id").GetUInt64(), oracle.GetProperty("l2_chain_id").GetUInt64(), Address.Zero, default,
            oracle.GetProperty("l2_block_time_seconds").GetUInt64());
        DerivedBlock[] derived = BatchDerivation.Derive(DaPayloadCodec.Decode(batch.CallData), ProducingSlice.OfSettledRun(0, batch.Entries.Length),
            systemNonce, context.ChainId, context.RollupId);
        return (recorded, derived, context);
    }

    private static DerivedBlock[] Derive(ProducingSlice slice, byte[] payload) =>
        BatchDerivation.Derive(DaPayloadCodec.Decode(payload), slice, SyncSettlementFixture.StartingNonce, SyncSettlementFixture.ChainId,
            SyncSettlementFixture.RollupId);

    /// <summary>The block before the window, which the first block's witness carries.</summary>
    private static BlockHeader WindowParent()
    {
        using Witness witness = StatelessFixtures.ReadWitness(Window, $"witness-{From}.json");
        return witness.Headers.Select(static h => Rlp.Decode<BlockHeader>(h)!).Single(static h => h.Number == From - 1);
    }

    private static object Inputs(BlockHeader header) => new
    {
        header.ParentHash,
        header.Number,
        header.Beneficiary,
        ExtraData = Convert.ToHexString(header.ExtraData),
        header.Timestamp,
        header.GasLimit,
        header.MixHash,
        header.Nonce,
        header.Difficulty,
        header.UnclesHash,
        header.ParentBeaconBlockRoot,
        header.BaseFeePerGas,
        header.ExcessBlobGas,
    };
}
