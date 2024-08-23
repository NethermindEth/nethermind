// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Test;
using static Nethermind.Merge.AuRa.Test.AuRaMergeEngineModuleTests;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using System.Threading;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Crypto;
using Nethermind.Blockchain.Receipts;
using Nethermind.Logging;
using Nethermind.Core.Specs;
using Nethermind.State;
using Nethermind.Shutter.Config;
using Nethermind.Blockchain.Find;

namespace Nethermind.Shutter.Test;

[TestFixture]
public class ShutterApiTests : EngineModuleTests
{
    private class ShutterApiSimulatorNoBlockTimeout(IAbiEncoder abiEncoder, IReadOnlyBlockTree blockTree, IEthereumEcdsa ecdsa, ILogFinder logFinder, IReceiptStorage receiptStorage, ILogManager logManager, ISpecProvider specProvider, ITimestamper timestamper, IWorldStateManager worldStateManager, IShutterConfig cfg, Dictionary<ulong, byte[]> validatorsInfo, Random rnd) : ShutterApiSimulator(abiEncoder, blockTree, ecdsa, logFinder, receiptStorage, logManager, specProvider, timestamper, worldStateManager, cfg, validatorsInfo, rnd)
    {
        public override TimeSpan BlockWaitCutoff { get => TimeSpan.MaxValue; }

        protected override ShutterTime InitTime(ISpecProvider specProvider, ITimestamper timestamper)
        {
            return new(1, timestamper, SlotLength, BlockUpToDateCutoff);
        }
    }

    [Test]
    public async Task Can_load_when_keys_arrive_before_block()
    {
        // genesis has unix timestamp 1
        ulong buildingSlot = ShutterTestsCommon.InitialSlot;
        ulong buildingSlotTimestamp = 1 + ShutterTestsCommon.InitialSlot * (ulong)ShutterApi.SlotLength.TotalSeconds;

        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(buildingSlotTimestamp - 5, 0);
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = buildingSlotTimestamp
        };

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        ShutterApiSimulatorNoBlockTimeout api = InitApi(rnd, chain, timestamper);

        ExecutionPayload initialPayload = CreateParentBlockRequestOnHead(chain.BlockTree);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, (int)buildingSlot - 2, initialPayload, true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];


        api.SetEventSimulator(ShutterTestsCommon.InitEventSimulator(rnd, 0, 10, ShutterTestsCommon.InitialTxPointer, api.TxLoader.GetAbi()));

        // keys arrive, waits for head block before loading
        api.AdvanceSlot(20);

        _ = Task.Run(async () =>
        {
            // block is a bit late
            await Task.Delay(1000);
            IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
            lastPayload = payloads[0];

            // Block newHeadBlock = Build.A.Block.WithNumber(chain.BlockTree!.Head!.Number).WithTimestamp(_buildingSlotTimestamp - 5).TestObject;
            api.TriggerNewHeadBlock(new(chain.BlockTree!.Head!));
        });

        // no events loaded initially
        var txs = api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(0));

        await api.TxSource.WaitForTransactions(ShutterTestsCommon.InitialSlot, new CancellationToken());
        txs = api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(20));
    }

    [Test]
    public async Task Can_load_when_previous_missed()
    {
        ulong buildingSlotTimestamp = ShutterTestsCommon.GenesisTimestamp + ShutterTestsCommon.InitialSlot * (ulong)ShutterApi.SlotLength.TotalSeconds;

        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(buildingSlotTimestamp - 5, 0);
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = buildingSlotTimestamp
        };

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, chain, timestamper);

        api.SetEventSimulator(ShutterTestsCommon.InitEventSimulator(rnd, 0, 10, ShutterTestsCommon.InitialTxPointer, api.TxLoader.GetAbi()));

        // waits to load transactions for head block
        api.AdvanceSlot(20);

        // no events loaded initially
        var txs = api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(0));

        // allow waiting for block to timeout
        await Task.Delay((int)api.BlockWaitCutoff.TotalMilliseconds + 200);

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        txs = api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(20));
    }


    [Test]
    public async Task Can_load_when_block_arrives_before_keys()
    {
        ulong buildingSlotTimestamp = ShutterTestsCommon.GenesisTimestamp + ShutterTestsCommon.InitialSlot * (ulong)ShutterApi.SlotLength.TotalSeconds;

        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(buildingSlotTimestamp - 5, 0);
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = buildingSlotTimestamp
        };

        using MergeTestBlockchain chain = await new MergeAuRaTestBlockchain(null, null, true).Build(ShutterTestsCommon.SpecProvider);
        IEngineRpcModule rpc = CreateEngineModule(chain);
        IReadOnlyList<ExecutionPayload> executionPayloads = await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true, null, 5);
        ExecutionPayload lastPayload = executionPayloads[executionPayloads.Count - 1];

        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, chain, timestamper);

        api.SetEventSimulator(ShutterTestsCommon.InitEventSimulator(rnd, 0, 10, ShutterTestsCommon.InitialTxPointer, api.TxLoader.GetAbi()));

        // no events loaded initially
        var txs = api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(0));

        api.TriggerNewHeadBlock(new(Build.A.Block.WithTimestamp(buildingSlotTimestamp - 5).TestObject));
        timestamper.SetDate((long)buildingSlotTimestamp);
        api.AdvanceSlot(20);

        IReadOnlyList<ExecutionPayload> payloads = await ProduceBranchV1(rpc, chain, 1, lastPayload, true, null, 5);
        lastPayload = payloads[0];

        await Task.Delay(100);

        txs = api.TxSource.GetTransactions(chain.BlockTree!.Head!.Header, 0, payloadAttributes).ToList();
        Assert.That(txs, Has.Count.EqualTo(20));
    }

    private static ShutterApiSimulatorNoBlockTimeout InitApi(Random rnd, MergeTestBlockchain chain, ITimestamper timestamper)
        => new(
            ShutterTestsCommon.AbiEncoder, chain.BlockTree.AsReadOnly(), chain.EthereumEcdsa, chain.LogFinder, chain.ReceiptStorage,
            chain.LogManager, chain.SpecProvider, timestamper, chain.WorldStateManager, ShutterTestsCommon.Cfg, [], rnd
        );
}
