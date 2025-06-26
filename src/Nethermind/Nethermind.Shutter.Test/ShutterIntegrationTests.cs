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
using System.Threading;
using Nethermind.Merge.Plugin.Test;

namespace Nethermind.Shutter.Test;

[TestFixture]
public class ShutterIntegrationTests : BaseEngineModuleTests
{
    private const int BuildingSlot = (int)ShutterTestsCommon.InitialSlot;
    private const ulong BuildingSlotTimestamp = ShutterTestsCommon.InitialSlotTimestamp;

    [Test]
    public async Task Can_load_when_previous_block_arrives_late()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(BuildingSlotTimestamp - 5, 0);
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = BuildingSlotTimestamp
        };

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd, timestamper).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, BuildingSlot - 2, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[^1];

        // keys arrive 5 seconds before slot start
        // waits for previous block to timeout then loads txs
        chain.Api!.AdvanceSlot(20);

        // no events loaded initially
        var txs = chain.Api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(0));

        // after timeout they should be loaded
        using CancellationTokenSource cts = new();
        await chain.Api.TxSource.WaitForTransactions((ulong)BuildingSlot, cts.Token);
        txs = chain.Api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(20));

        // late block arrives, then next block should contain loaded transactions
        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 2, lastPayload, true, null, 5);
        lastPayload = payloads[^1];
        Block? b = lastPayload.TryGetBlock().Block;
        Assert.That(b!.Transactions, Has.Length.EqualTo(20));
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

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd, timestamper).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, BuildingSlot - 2, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        // no events loaded initially
        var txs = chain.Api!.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(0));

        chain.Api.AdvanceSlot(20);

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        txs = chain.Api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(20));

        payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];
        Block? b = lastPayload.TryGetBlock().Block;
        Assert.That(b!.Transactions, Has.Length.EqualTo(20));
    }

    [Test]
    [NonParallelizable]
    public async Task Can_increment_metric_on_missed_keys()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        long time = 1;
        Timestamper timestamper = new(time);

        Metrics.ShutterKeysMissed = 0;

        using var chain = (ShutterTestBlockchain)await new ShutterTestBlockchain(rnd, timestamper).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);

        ExecutionPayload lastPayload = CreateParentBlockRequestOnHead(chain.BlockTree);
        for (int i = 0; i < 5; i++)
        {
            // KeysMissed will be incremented when get_payload is called
            lastPayload = (await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5))[0];

            time += (long)ShutterTestsCommon.SlotLength.TotalSeconds;
        }

        Assert.That(Metrics.ShutterKeysMissed, Is.EqualTo(5));
    }

}
