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
        MergeConfig mergeConfig = new() { Enabled = true, SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(mergeConfig);
        IBoostRelay boostRelay = Substitute.For<IBoostRelay>();
        boostRelay.GetPayloadAttributes(Arg.Any<PayloadAttributes>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                PayloadAttributes payloadAttributes = c.Arg<PayloadAttributes>();
                payloadAttributes.SuggestedFeeRecipient = TestItem.AddressA;
                payloadAttributes.PrevRandao = TestItem.KeccakA;
                payloadAttributes.Timestamp += 1;
                return payloadAttributes;
            });
        
        BoostBlockImprovementContextFactory improvementContextFactory = new(chain.BlockProductionTrigger, TimeSpan.FromSeconds(5), boostRelay, chain.StateReader);
        TimeSpan timePerSlot = TimeSpan.FromSeconds(10);
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            chain.SealEngine, 
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

        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
            .PayloadId!;

        
        ResultWrapper<ExecutionPayloadV1?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        ExecutionPayloadV1 executionPayloadV1 = response.Data!;
        executionPayloadV1.FeeRecipient.Should().Be(TestItem.AddressA);
        executionPayloadV1.PrevRandao.Should().Be(TestItem.KeccakA);
        executionPayloadV1.Should().BeEquivalentTo(sentItem!.Block);
        sentItem.Profit.Should().Be(0);
    }
    
    [Test]
    public async Task forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http()
    {
        MergeConfig mergeConfig = new() { Enabled = true, SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(mergeConfig);
        IJsonSerializer serializer = chain.JsonSerializer;
        
        UInt256 timestamp = Timestamper.UnixTime.Seconds;
        PayloadAttributes payloadAttributes = new() { Timestamp = timestamp, SuggestedFeeRecipient = Address.Zero, PrevRandao = Keccak.Zero };
        
        string relayUrl = "http://localhost";
        MockHttpMessageHandler mockHttp = new();
        mockHttp.Expect(HttpMethod.Post, relayUrl + BoostRelay.GetPayloadAttributesPath)
            .WithContent("{\"timestamp\":\"0x3e8\",\"prevRandao\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"suggestedFeeRecipient\":\"0x0000000000000000000000000000000000000000\"}")
            .Respond("application/json", "{\"timestamp\":\"0x3e9\",\"prevRandao\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"suggestedFeeRecipient\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"}");
        
        mockHttp.Expect(HttpMethod.Post, relayUrl + BoostRelay.SendPayloadPath)
            .WithContent("{\"block\":{\"parentHash\":\"0x1c53bdbf457025f80c6971a9cf50986974eed02f0a9acaeeb49cafef10efd133\",\"feeRecipient\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"prevRandao\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"timestamp\":\"0x3e9\",\"extraData\":\"0x\",\"baseFeePerGas\":\"0x0\",\"blockHash\":\"0xb519d89363b891216dbf9bd046f226cae9348a90ba0db00e7ade437fff7a1510\",\"transactions\":[]},\"profit\":\"0x0\"}");
        
        DefaultHttpClient defaultHttpClient = new(mockHttp.ToHttpClient(), serializer, chain.LogManager, 1, 100);
        BoostRelay boostRelay = new(defaultHttpClient, relayUrl);
        BoostBlockImprovementContextFactory improvementContextFactory = new(chain.BlockProductionTrigger, TimeSpan.FromSeconds(5000), boostRelay, chain.StateReader);
        TimeSpan timePerSlot = TimeSpan.FromSeconds(1000);
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            chain.SealEngine, 
            TimerFactory.Default, 
            chain.LogManager, 
            timePerSlot);
            
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        
        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                payloadAttributes).Result.Data
            .PayloadId!;

        ResultWrapper<ExecutionPayloadV1?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));
        
        ExecutionPayloadV1 executionPayloadV1 = response.Data!;
        executionPayloadV1.FeeRecipient.Should().Be(TestItem.AddressA);
        executionPayloadV1.PrevRandao.Should().Be(TestItem.KeccakA);
        
        mockHttp.VerifyNoOutstandingExpectation();
    }
}
