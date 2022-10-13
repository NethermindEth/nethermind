//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.IO;
using System.Net.Http;
using System.Text;
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
using Nethermind.Merge.Plugin.Data.V1;
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
        using MergeTestBlockchain chain = await CreateBlockChain(mergeConfig);
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
        UInt256 timestamp = Timestamper.UnixTime.Seconds;
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

        ResultWrapper<ExecutionPayloadV1?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        ExecutionPayloadV1 executionPayloadV1 = response.Data!;
        executionPayloadV1.FeeRecipient.Should().Be(TestItem.AddressA);
        executionPayloadV1.PrevRandao.Should().Be(TestItem.KeccakA);
        executionPayloadV1.GasLimit.Should().Be(10_000_000L);
        executionPayloadV1.Should().BeEquivalentTo(sentItem!.Block);
        sentItem.Profit.Should().Be(0);
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public virtual async Task forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http()
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(mergeConfig);
        IJsonSerializer serializer = chain.JsonSerializer;

        UInt256 timestamp = Timestamper.UnixTime.Seconds;
        PayloadAttributes payloadAttributes = new() { Timestamp = timestamp, SuggestedFeeRecipient = Address.Zero, PrevRandao = Keccak.Zero };

        string relayUrl = "http://localhost";
        MockHttpMessageHandler mockHttp = new();
        mockHttp.Expect(HttpMethod.Post, relayUrl + BoostRelay.GetPayloadAttributesPath)
            .WithContent("{\"timestamp\":\"0x3e8\",\"prevRandao\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"suggestedFeeRecipient\":\"0x0000000000000000000000000000000000000000\"}")
            .Respond("application/json", "{\"timestamp\":\"0x3e9\",\"prevRandao\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"suggestedFeeRecipient\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"}");

        //TODO: think about extracting an essely serialisable class, test its serializatoin sepratly, refactor with it similar methods like the one above
        string expected_parentHash = "0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133";
        string expected_feeRecipient = "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099";
        string expected_stateRoot = "0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f";
        string expected_receiptsRoot = "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421";
        string expected_logsBloom = "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
        string expected_prevRandao = "0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760";
        string expected_blockNumber = "0x1";
        string expected_gasLimit = "0x3d0900";
        string expected_gasUsed = "0x0";
        string expected_timestamp = "0x3e9";
        string expected_extraData = "0x4e65746865726d696e64"; // Nethermind
        string expected_baseFeePerGas = "0x0";
        string expected_blockHash = "0x4ced29c819cf146b41ef448042773f958d5bbe297b0d6b82be677b65c85b436b";
        string expected_transactions = "[]";
        string expected_profit = "0x0";


        string expectedContent = "{\"block\":{\"parentHash\":\"" +
                                 expected_parentHash +
                                 "\",\"feeRecipient\":\"" +
                                 expected_feeRecipient +
                                 "\",\"stateRoot\":\"" +
                                 expected_stateRoot +
                                 "\",\"receiptsRoot\":\"" +
                                 expected_receiptsRoot +
                                 "\",\"logsBloom\":\"" +
                                 expected_logsBloom +
                                 "\",\"prevRandao\":\"" +
                                 expected_prevRandao +
                                 "\",\"blockNumber\":\"" +
                                 expected_blockNumber +
                                 "\",\"gasLimit\":\"" +
                                 expected_gasLimit +
                                 "\",\"gasUsed\":\"" +
                                 expected_gasUsed +
                                 "\",\"timestamp\":\"" +
                                 expected_timestamp +
                                 "\",\"extraData\":\"" +
                                 expected_extraData +
                                 "\",\"baseFeePerGas\":\"" +
                                 expected_baseFeePerGas +
                                 "\",\"blockHash\":\"" +
                                 expected_blockHash +
                                 "\",\"transactions\":" +
                                 expected_transactions +
                                 "},\"profit\":\"" +
                                 expected_profit +
                                 "\"}";

        mockHttp.Expect(HttpMethod.Post, relayUrl + BoostRelay.SendPayloadPath)
            .WithContent(expectedContent);

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

        ResultWrapper<ExecutionPayloadV1?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        ExecutionPayloadV1 executionPayloadV1 = response.Data!;
        executionPayloadV1.FeeRecipient.Should().Be(TestItem.AddressA);
        executionPayloadV1.PrevRandao.Should().Be(TestItem.KeccakA);

        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Test]
    public async Task forkchoiceUpdatedV1_should_ignore_gas_limit([Values(false, true)] bool relay)
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(mergeConfig);
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
        UInt256 timestamp = Timestamper.UnixTime.Seconds;
        Keccak random = Keccak.Zero;
        Address feeRecipient = Address.Zero;

        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random, GasLimit = 10_000_000L }).Result.Data
            .PayloadId!;

        ResultWrapper<ExecutionPayloadV1?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        ExecutionPayloadV1 executionPayloadV1 = response.Data!;
        executionPayloadV1.GasLimit.Should().Be(4_000_000L);
    }
}
