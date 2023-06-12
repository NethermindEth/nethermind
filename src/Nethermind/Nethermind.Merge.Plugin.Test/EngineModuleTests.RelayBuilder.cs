// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Facade.Proxy;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using RichardSzalay.MockHttp;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task forkchoiceUpdatedV1_should_communicate_with_boost_relay()
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(null, mergeConfig);
        IBoostRelay boostRelay = Substitute.For<IBoostRelay>();
        boostRelay.GetPayloadAttributes(Arg.Any<PayloadAttributes>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                PayloadAttributes payloadAttributes = c.Arg<PayloadAttributes>();
                payloadAttributes.SuggestedFeeRecipient = TestItem.AddressA;
                payloadAttributes.PrevRandao = TestItem.KeccakA;
                payloadAttributes.Timestamp += 1;
                payloadAttributes.GasLimit = 10_000_000L;
                return payloadAttributes;
            });

        BoostBlockImprovementContextFactory improvementContextFactory = new(chain.BlockProductionTrigger, TimeSpan.FromSeconds(5), boostRelay, chain.StateReader);
        TimeSpan timePerSlot = TimeSpan.FromSeconds(10);
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            timePerSlot);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Keccak random = Keccak.Zero;
        Address feeRecipient = Address.Zero;

        BoostExecutionPayloadV1? sentItem = null;
        boostRelay.When(b => b.SendPayload(Arg.Any<BoostExecutionPayloadV1>(), Arg.Any<CancellationToken>()))
            .Do(c => sentItem = c.Arg<BoostExecutionPayloadV1>());

        ManualResetEvent wait = new(false);
        chain.PayloadPreparationService.BlockImproved += (_, _) => wait.Set();

        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
            .PayloadId!;

        await wait.WaitOneAsync(100, CancellationToken.None);

        ResultWrapper<ExecutionPayload?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        ExecutionPayload executionPayloadV1 = response.Data!;
        executionPayloadV1.FeeRecipient.Should().Be(TestItem.AddressA);
        executionPayloadV1.PrevRandao.Should().Be(TestItem.KeccakA);
        executionPayloadV1.GasLimit.Should().Be(10_000_000L);
        executionPayloadV1.Should().BeEquivalentTo(sentItem!.Block);
        sentItem.Profit.Should().Be(0);
    }

    [TestCase(
        "0x4ced29c819cf146b41ef448042773f958d5bbe297b0d6b82be677b65c85b436b",
        "0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133")]
    [Parallelizable(ParallelScope.None)]
    public virtual async Task forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http(
        string blockHash, string parentHash)
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(null, mergeConfig);
        IJsonSerializer serializer = chain.JsonSerializer;

        ulong timestamp = Timestamper.UnixTime.Seconds;
        PayloadAttributes payloadAttributes = new() { Timestamp = timestamp, SuggestedFeeRecipient = Address.Zero, PrevRandao = Keccak.Zero };

        string relayUrl = "http://localhost";
        MockHttpMessageHandler mockHttp = new();
        mockHttp.Expect(HttpMethod.Post, relayUrl + BoostRelay.GetPayloadAttributesPath)
            .WithContent("{\"timestamp\":\"0x3e8\",\"prevRandao\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"suggestedFeeRecipient\":\"0x0000000000000000000000000000000000000000\"}")
            .Respond("application/json", "{\"timestamp\":\"0x3e9\",\"prevRandao\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"suggestedFeeRecipient\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"}");

        //TODO: think about extracting an essely serialisable class, test its serializatoin sepratly, refactor with it similar methods like the one above
        var expected_parentHash = parentHash;
        var expected_feeRecipient = "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099";
        var expected_stateRoot = "0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f";
        var expected_receiptsRoot = "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421";
        var expected_logsBloom = "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
        var expected_prevRandao = "0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760";
        var expected_blockNumber = 1;
        var expected_gasLimit = 0x3d0900L;
        var expected_gasUsed = 0;
        var expected_timestamp = 0x3e9UL;
        var expected_extraData = "0x4e65746865726d696e64"; // Nethermind
        var expected_baseFeePerGas = (UInt256)0;
        var expected_blockHash = blockHash;
        var expected_profit = "0x0";

        var expected = new BoostExecutionPayloadV1
        {
            Block = new ExecutionPayload
            {
                ParentHash = new(expected_parentHash),
                FeeRecipient = new(expected_feeRecipient),
                StateRoot = new(expected_stateRoot),
                ReceiptsRoot = new(expected_receiptsRoot),
                LogsBloom = new(Bytes.FromHexString(expected_logsBloom)),
                PrevRandao = new(expected_prevRandao),
                BlockNumber = expected_blockNumber,
                GasLimit = expected_gasLimit,
                GasUsed = expected_gasUsed,
                Timestamp = expected_timestamp,
                ExtraData = Bytes.FromHexString(expected_extraData),
                BaseFeePerGas = expected_baseFeePerGas,
                BlockHash = new(expected_blockHash),
                Transactions = Array.Empty<byte[]>()
            },
            Profit = UInt256.Parse(expected_profit[2..], NumberStyles.HexNumber)
        };

        mockHttp
            .Expect(HttpMethod.Post, relayUrl + BoostRelay.SendPayloadPath)
            .WithContent(serializer.Serialize(expected));

        DefaultHttpClient defaultHttpClient = new(mockHttp.ToHttpClient(), serializer, chain.LogManager, 1, 100);
        BoostRelay boostRelay = new(defaultHttpClient, relayUrl);
        BoostBlockImprovementContextFactory improvementContextFactory = new(chain.BlockProductionTrigger, TimeSpan.FromSeconds(5000), boostRelay, chain.StateReader);
        TimeSpan timePerSlot = TimeSpan.FromSeconds(1000);
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            timePerSlot);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;

        ManualResetEvent wait = new(false);
        chain.PayloadPreparationService.BlockImproved += (_, _) => wait.Set();

        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                payloadAttributes).Result.Data
            .PayloadId!;

        await wait.WaitOneAsync(100, CancellationToken.None);

        ResultWrapper<ExecutionPayload?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        ExecutionPayload executionPayloadV1 = response.Data!;
        executionPayloadV1.FeeRecipient.Should().Be(TestItem.AddressA);
        executionPayloadV1.PrevRandao.Should().Be(TestItem.KeccakA);

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Test]
    public async Task forkchoiceUpdatedV1_should_ignore_gas_limit([Values(false, true)] bool relay)
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(null, mergeConfig);
        IBlockImprovementContextFactory improvementContextFactory;
        if (relay)
        {
            IBoostRelay boostRelay = Substitute.For<IBoostRelay>();
            boostRelay.GetPayloadAttributes(Arg.Any<PayloadAttributes>(), Arg.Any<CancellationToken>())
                .Returns(c => c.Arg<PayloadAttributes>());

            improvementContextFactory = new BoostBlockImprovementContextFactory(chain.BlockProductionTrigger, TimeSpan.FromSeconds(5), boostRelay, chain.StateReader);
        }
        else
        {
            improvementContextFactory = new BlockImprovementContextFactory(chain.BlockProductionTrigger, TimeSpan.FromSeconds(5));
        }

        TimeSpan timePerSlot = TimeSpan.FromSeconds(10);
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            timePerSlot);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Keccak random = Keccak.Zero;
        Address feeRecipient = Address.Zero;

        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random, GasLimit = 10_000_000L }).Result.Data
            .PayloadId!;

        ResultWrapper<ExecutionPayload?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        ExecutionPayload executionPayloadV1 = response.Data!;
        executionPayloadV1.GasLimit.Should().Be(4_000_000L);
    }
}
