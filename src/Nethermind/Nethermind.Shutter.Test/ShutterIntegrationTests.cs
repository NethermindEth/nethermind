// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using System.Threading;
using Nethermind.Merge.Plugin.Test;

namespace Nethermind.Shutter.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ShutterIntegrationTests : BaseEngineModuleTests
{
    private const int BuildingSlot = (int)ShutterTestsCommon.InitialSlot;
    private const ulong BuildingSlotTimestamp = ShutterTestsCommon.InitialSlotTimestamp;
    private const int ShutterTxCount = 20;

    [Test]
    public async Task Can_load_when_previous_block_arrives_late()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(BuildingSlotTimestamp - 5, 0);
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = BuildingSlotTimestamp
        };

        using ShutterTestBlockchain chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd, timestamper).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, BuildingSlot - 2, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[^1];

        // keys arrive 5 seconds before slot start
        // waits for previous block to timeout then loads txs
        chain.Api.AdvanceSlot(ShutterTxCount);

        // no events loaded initially
        BlockHeader parent = chain.BlockTree!.Head!.Header;
        List<Transaction> txs = chain.Api.TxSource.GetTransactions(parent, parent.CreateSimulatedChild(payloadAttributes.Timestamp), 0, payloadAttributes).ToList();
        Assert.That(txs.Count, Is.EqualTo(0));

        // after timeout they should be loaded
        using CancellationTokenSource cts = new();
        await chain.Api.TxSource.WaitForTransactions(BuildingSlot, cts.Token);
        parent = chain.BlockTree.Head!.Header;
        txs = chain.Api.TxSource.GetTransactions(parent, parent.CreateSimulatedChild(payloadAttributes.Timestamp), 0, payloadAttributes).ToList();
        Assert.That(txs.Count, Is.EqualTo(ShutterTxCount));

        // late block arrives, then next block should contain loaded transactions
        lastPayload = (await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5))[0];
        Block b = await BuildBlockWithShutterTxs(rpc, chain, lastPayload);
        Assert.That(b.Transactions, Has.Length.EqualTo(ShutterTxCount));
    }


    [Test]
    public async Task Can_load_when_block_arrives_before_keys()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(BuildingSlotTimestamp, 0);
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = BuildingSlotTimestamp
        };

        using ShutterTestBlockchain chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd, timestamper).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, BuildingSlot - 2, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[^1];

        // no events loaded initially
        BlockHeader parent = chain.BlockTree.Head!.Header;
        List<Transaction> txs = chain.Api.TxSource.GetTransactions(parent, parent.CreateSimulatedChild(payloadAttributes.Timestamp), 0, payloadAttributes).ToList();
        Assert.That(txs.Count, Is.EqualTo(0));

        chain.Api.AdvanceSlot(ShutterTxCount);

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        parent = chain.BlockTree.Head!.Header;
        txs = chain.Api.TxSource.GetTransactions(parent, parent.CreateSimulatedChild(payloadAttributes.Timestamp), 0, payloadAttributes).ToList();
        Assert.That(txs.Count, Is.EqualTo(ShutterTxCount));

        Block b = await BuildBlockWithShutterTxs(rpc, chain, lastPayload);
        Assert.That(b.Transactions, Has.Length.EqualTo(ShutterTxCount));
    }

    [Test]
    [NonParallelizable]
    public async Task Can_increment_metric_on_missed_keys()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        long time = 1;
        Timestamper timestamper = new(time);

        ulong missedBefore = Metrics.ShutterKeysMissed;

        using ShutterTestBlockchain chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd, timestamper).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayload lastPayload = CreateParentBlockRequestOnHead(chain.BlockTree);
        for (int i = 0; i < 5; i++)
        {
            // KeysMissed will be incremented when get_payload is called
            lastPayload = (await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5))[0];

            time += (long)ShutterTestsCommon.SlotLength.TotalSeconds;
        }

        // ImproveBlock tasks run in the background and may not have completed yet
        // when GetPayload returns (it does not wait for them), so poll until all increments land.
        Assert.That(() => Metrics.ShutterKeysMissed - missedBefore, Is.EqualTo((ulong)5).After(5000, 50));
    }

    /// <summary>Builds the next block on <paramref name="parent"/> once the Shutter transactions are in it.</summary>
    /// <remarks>
    /// <c>getPayload</c> does not wait for a pending improvement before returning the empty block, so the improvement is awaited first.
    /// </remarks>
    private static async Task<Block> BuildBlockWithShutterTxs(IEngineRpcModule rpc, ShutterTestBlockchain chain, ExecutionPayload parent)
    {
        await chain.WaitForCommitted(parent.BlockHash);
        Task improved = chain.WaitForImprovedBlock(parent.BlockHash, ShutterTxCount);
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = parent.Timestamp + 5,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero
        };
        string payloadId = chain.PayloadPreparationService.StartPreparingPayload(chain.BlockTree.FindHeader(parent.BlockHash)!, payloadAttributes)!;
        await improved;
        return (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!.TryGetBlock().Data!;
    }

}
