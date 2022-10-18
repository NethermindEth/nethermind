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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task getPayloadV1_should_allow_asking_multiple_times_by_same_payload_id()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        Keccak startingHead = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceState = new(startingHead, Keccak.Zero, startingHead);
        PayloadAttributes payload = new() { Timestamp = Timestamper.UnixTime.Seconds, SuggestedFeeRecipient = Address.Zero, PrevRandao = Keccak.Zero };
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> forkchoiceResponse = rpc.engine_forkchoiceUpdatedV1(forkchoiceState, payload);
        byte[] payloadId = Bytes.FromHexString(forkchoiceResponse.Result.Data.PayloadId!);
        ResultWrapper<ExecutionPayloadV1?> responseFirst = await rpc.engine_getPayloadV1(payloadId);
        responseFirst.Should().NotBeNull();
        responseFirst.Result.ResultType.Should().Be(ResultType.Success);
        ResultWrapper<ExecutionPayloadV1?> responseSecond = await rpc.engine_getPayloadV1(payloadId);
        responseSecond.Should().NotBeNull();
        responseSecond.Result.ResultType.Should().Be(ResultType.Success);
        responseSecond.Data!.BlockHash!.Should().Be(responseFirst.Data!.BlockHash!);
    }

    [Test]
    public async Task getPayloadV1_should_return_error_if_called_after_cleanup_timer()
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(mergeConfig);
        BlockImprovementContextFactory improvementContextFactory = new(chain.BlockProductionTrigger, TimeSpan.FromSeconds(1));
        TimeSpan timePerSlot = TimeSpan.FromMilliseconds(10);
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
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
            .PayloadId!;

        await Task.Delay(PayloadPreparationService.SlotsPerOldPayloadCleanup * 2 * timePerSlot + timePerSlot);

        ResultWrapper<ExecutionPayloadV1?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        response.ErrorCode.Should().Be(MergeErrorCodes.UnknownPayload);
    }

    [Test]
    public async Task getPayloadV1_picks_transactions_from_pool_v1()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        uint count = 3;
        int value = 10;
        Address recipient = TestItem.AddressF;
        PrivateKey sender = TestItem.PrivateKeyB;
        Transaction[] transactions = BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);
        chain.AddTransactions(transactions);
        chain.PayloadPreparationService!.BlockImproved += (_, _) => { blockImprovementLock.Release(1); };
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes() { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;

        await blockImprovementLock.WaitAsync(10000);
        ExecutionPayloadV1 getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        getPayloadResult.StateRoot.Should().NotBe(chain.BlockTree.Genesis!.StateRoot!);

        Transaction[] transactionsInBlock = getPayloadResult.GetTransactions();
        transactionsInBlock.Should().BeEquivalentTo(transactions, o => o
            .Excluding(t => t.ChainId)
            .Excluding(t => t.SenderAddress)
            .Excluding(t => t.Timestamp)
            .Excluding(t => t.PoolIndex)
            .Excluding(t => t.GasBottleneck));

        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        UInt256 totalValue = ((int)(count * value)).GWei();
        chain.StateReader.GetBalance(getPayloadResult.StateRoot, recipient).Should().Be(totalValue);
    }

    public static IEnumerable WaitTestCases
    {
        get
        {
            yield return new TestCaseData(PayloadPreparationService.GetPayloadWaitForFullBlockMillisecondsDelay / 10) { ExpectedResult = 3, TestName = "Production manages to finish" };
            yield return new TestCaseData(PayloadPreparationService.GetPayloadWaitForFullBlockMillisecondsDelay * 2) { ExpectedResult = 0, TestName = "Production takes too long" };
        }
    }

    [TestCaseSource(nameof(WaitTestCases))]
    public async Task<int> getPayloadV1_waits_for_block_production(TimeSpan delay)
    {
        using MergeTestBlockchain chain = await CreateBlockChain();

        DelayBlockImprovementContextFactory improvementContextFactory = new(chain.BlockProductionTrigger, TimeSpan.FromSeconds(10), delay);
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            TimeSpan.FromSeconds(10));

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        uint count = 3;
        int value = 10;
        Address recipient = TestItem.AddressF;
        PrivateKey sender = TestItem.PrivateKeyB;
        Transaction[] transactions = BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);
        chain.AddTransactions(transactions);
        string payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;

        ExecutionPayloadV1 getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        return getPayloadResult.Transactions.Length;
    }

    [Test]
    public async Task getPayloadV1_return_correct_block_values_for_empty_block()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        Keccak? random = TestItem.KeccakF;
        UInt256 timestamp = chain.BlockTree.Head!.Timestamp + 5;
        Address? suggestedFeeRecipient = TestItem.AddressC;
        PayloadAttributes? payloadAttributes = new() { PrevRandao = random, Timestamp = timestamp, SuggestedFeeRecipient = suggestedFeeRecipient };
        ExecutionPayloadV1 getPayloadResult = await BuildAndGetPayloadResult(chain, rpc, payloadAttributes);
        getPayloadResult.ParentHash.Should().Be(startingHead);


        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        BlockHeader? currentHeader = chain.BlockTree.BestSuggestedHeader!;

        Assert.AreEqual("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347", currentHeader.UnclesHash!.ToString());
        Assert.AreEqual((UInt256)0, currentHeader.Difficulty);
        Assert.AreEqual(0, currentHeader.Nonce);
        Assert.AreEqual(random, currentHeader.MixHash);
    }

    [Test]
    public async Task getPayloadV1_should_return_error_if_there_was_no_corresponding_prepare_call()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        UInt256 timestamp = Timestamper.UnixTime.Seconds;
        Keccak random = Keccak.Zero;
        Address feeRecipient = Address.Zero;
        string _ = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
            .PayloadId!;

        byte[] requestedPayloadId = Bytes.FromHexString("0x45bd36a8143d860d");
        ResultWrapper<ExecutionPayloadV1?> response = await rpc.engine_getPayloadV1(requestedPayloadId);

        response.ErrorCode.Should().Be(MergeErrorCodes.UnknownPayload);
    }

    [Test]
    public async Task getPayload_correctlyEncodeTransactions()
    {
        byte[] payload = new byte[0];
        IPayloadPreparationService payloadPreparationService = Substitute.For<IPayloadPreparationService>();
        Block block = Build.A.Block.WithTransactions(
            Build.A.Transaction.WithTo(TestItem.AddressD).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithTo(TestItem.AddressD).WithType(TxType.EIP1559).WithMaxFeePerGas(20).SignedAndResolved(TestItem.PrivateKeyA).TestObject).TestObject;
        payloadPreparationService.GetPayload(Arg.Any<string>()).Returns(block);
        using MergeTestBlockchain chain = await CreateBlockChain(null, payloadPreparationService);

        IEngineRpcModule rpc = CreateEngineModule(chain);

        string result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", payload.ToHexString(true));
        Assert.AreEqual(result, "{\"jsonrpc\":\"2.0\",\"result\":{\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"feeRecipient\":\"0x0000000000000000000000000000000000000000\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"prevRandao\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"blockNumber\":\"0x0\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"timestamp\":\"0xf4240\",\"extraData\":\"0x010203\",\"baseFeePerGas\":\"0x0\",\"blockHash\":\"0x5fd61518405272d77fd6cdc8a824a109d75343e32024ee4f6769408454b1823d\",\"transactions\":[\"0xf85f800182520894475674cb523a0a2736b7f7534390288fce16982c018025a0634db2f18f24d740be29e03dd217eea5757ed7422680429bdd458c582721b6c2a02f0fa83931c9a99d3448a46b922261447d6a41d8a58992b5596089d15d521102\",\"0x02f8620180011482520894475674cb523a0a2736b7f7534390288fce16982c0180c001a0033e85439a128c42f2ba47ca278f1375ef211e61750018ff21bcd9750d1893f2a04ee981fe5261f8853f95c865232ffdab009abcc7858ca051fb624c49744bf18d\"]},\"id\":67}");
    }

    [Test]
    public async Task getPayload_should_serialize_unknown_payload_response_properly()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        byte[] payloadId = Bytes.FromHexString("0x1111111111111111");

        string parameters = payloadId.ToHexString(true);
        string result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", parameters);
        result.Should().Be("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-38001,\"message\":\"unknown payload\"},\"id\":67}");
    }

    [Test]
    [Retry(3)]
    public async Task consecutive_blockImprovements_should_be_disposed()
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        using MergeTestBlockchain chain = await CreateBlockChain(mergeConfig);
        StoringBlockImprovementContextFactory improvementContextFactory = new(new MockBlockImprovementContextFactory());
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 10 * delay;
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            timePerSlot,
            improvementDelay: delay,
            minTimeForProduction: delay);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        UInt256 timestamp = Timestamper.UnixTime.Seconds;
        Keccak random = Keccak.Zero;
        Address feeRecipient = Address.Zero;

        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data.PayloadId!;

        await Task.Delay(timePerSlot / 2);

        improvementContextFactory.CreatedContexts.Count.Should().BeInRange(3, 5);
        improvementContextFactory.CreatedContexts.Take(improvementContextFactory.CreatedContexts.Count - 1).Should().OnlyContain(i => i.Disposed);

        await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        improvementContextFactory.CreatedContexts.Should().OnlyContain(i => i.Disposed);
    }

    [Test, Retry(3)]
    public async Task getPayloadV1_picks_transactions_from_pool_constantly_improving_blocks()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockChain();
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 50 * delay;
        StoringBlockImprovementContextFactory improvementContextFactory = new(new BlockImprovementContextFactory(chain.BlockProductionTrigger, TimeSpan.FromSeconds(chain.MergeConfig.SecondsPerSlot)));
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            timePerSlot,
            improvementDelay: delay,
            minTimeForProduction: delay);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyB, TestItem.AddressF, 3, 10, out _, out _));
        chain.PayloadPreparationService!.BlockImproved += (_, _) => { blockImprovementLock.Release(1); };
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;

        await blockImprovementLock.WaitAsync(100 * TestContext.CurrentContext.CurrentRepeatCount);
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyC, TestItem.AddressA, 3, 10, out _, out _));

        await blockImprovementLock.WaitAsync(100 * TestContext.CurrentContext.CurrentRepeatCount);
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyA, TestItem.AddressC, 5, 10, out _, out _));

        await blockImprovementLock.WaitAsync(100 * TestContext.CurrentContext.CurrentRepeatCount);

        ExecutionPayloadV1 getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        List<int?> transactionsLength = improvementContextFactory.CreatedContexts
            .Select(c =>
                c.CurrentBestBlock?.Transactions.Length).ToList();

        transactionsLength.Should().Equal(3, 6, 11);
        Transaction[] txs = getPayloadResult.GetTransactions();

        txs.Should().HaveCount(11);
    }

    [Test]
    [Retry(3)]
    public async Task getPayloadV1_doesnt_wait_for_improvement_when_block_is_not_empty()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockChain();
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 50 * delay;
        StoringBlockImprovementContextFactory improvementContextFactory = new(new DelayBlockImprovementContextFactory(chain.BlockProductionTrigger, TimeSpan.FromSeconds(chain.MergeConfig.SecondsPerSlot), 3 * delay));
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            improvementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            timePerSlot,
            improvementDelay: delay,
            minTimeForProduction: delay);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyB, TestItem.AddressF, 3, 10, out _, out _));
        chain.PayloadPreparationService!.BlockImproved += (_, _) => { blockImprovementLock.Release(1); };
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;

        await blockImprovementLock.WaitAsync(100);
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyC, TestItem.AddressA, 3, 10, out _, out _));

        using SemaphoreSlim blockImprovementStartsLock = new(0);
        IBlockImprovementContext? cancelledContext = null;
        improvementContextFactory.ImprovementStarted += (_, e) =>
        {
            blockImprovementStartsLock.Release(1);
            cancelledContext = e.BlockImprovementContext;
        };

        await blockImprovementStartsLock.WaitAsync(100); // started improving block
        improvementContextFactory.CreatedContexts.Should().HaveCount(2);

        ExecutionPayloadV1 getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        getPayloadResult.GetTransactions().Should().HaveCount(3);
        cancelledContext?.Disposed.Should().BeTrue();
    }
}
