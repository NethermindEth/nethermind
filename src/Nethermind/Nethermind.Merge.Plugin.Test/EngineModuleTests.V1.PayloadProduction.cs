// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
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
using Nethermind.Merge.Plugin.Data;
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
        ResultWrapper<ExecutionPayload?> responseFirst = await rpc.engine_getPayloadV1(payloadId);
        responseFirst.Should().NotBeNull();
        responseFirst.Result.ResultType.Should().Be(ResultType.Success);
        ResultWrapper<ExecutionPayload?> responseSecond = await rpc.engine_getPayloadV1(payloadId);
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
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Keccak random = Keccak.Zero;
        Address feeRecipient = Address.Zero;

        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
            .PayloadId!;

        await Task.Delay(PayloadPreparationService.SlotsPerOldPayloadCleanup * 2 * timePerSlot + timePerSlot);

        ResultWrapper<ExecutionPayload?> response = await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

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
        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

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

        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        return getPayloadResult.Transactions.Length;
    }

    [Test]
    public async Task getPayloadV1_return_correct_block_values_for_empty_block()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        Keccak? random = TestItem.KeccakF;
        ulong timestamp = chain.BlockTree.Head!.Timestamp + 5;
        Address? suggestedFeeRecipient = TestItem.AddressC;
        PayloadAttributes? payloadAttributes = new() { PrevRandao = random, Timestamp = timestamp, SuggestedFeeRecipient = suggestedFeeRecipient };
        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc, payloadAttributes);
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
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Keccak random = Keccak.Zero;
        Address feeRecipient = Address.Zero;
        string _ = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data
            .PayloadId!;

        byte[] requestedPayloadId = Bytes.FromHexString("0x45bd36a8143d860d");
        ResultWrapper<ExecutionPayload?> response = await rpc.engine_getPayloadV1(requestedPayloadId);

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
        IBlockProductionContext improvementContext = Substitute.For<IBlockProductionContext>();
        improvementContext.CurrentBestBlock.Returns(block);
        payloadPreparationService.GetPayload(Arg.Any<string>()).Returns(improvementContext);
        using MergeTestBlockchain chain = await CreateBlockChain(null, payloadPreparationService);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        string result = RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", payload.ToHexString(true));
        Assert.AreEqual(result,
            chain.JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                result = new ExecutionPayload
                {
                    BaseFeePerGas = 0,
                    BlockHash = new("0x5fd61518405272d77fd6cdc8a824a109d75343e32024ee4f6769408454b1823d"),
                    BlockNumber = 0,
                    ExtraData = Bytes.FromHexString("0x010203"),
                    FeeRecipient = Address.Zero,
                    GasLimit = 0x3d0900L,
                    GasUsed = 0,
                    LogsBloom = new Bloom(Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")),
                    ParentHash = new("0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c"),
                    PrevRandao = new("0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2"),
                    ReceiptsRoot = new("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"),
                    StateRoot = new("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"),
                    Timestamp = 0xf4240UL,
                    Transactions = new[]
                    {
                        Bytes.FromHexString("0xf85f800182520894475674cb523a0a2736b7f7534390288fce16982c018025a0634db2f18f24d740be29e03dd217eea5757ed7422680429bdd458c582721b6c2a02f0fa83931c9a99d3448a46b922261447d6a41d8a58992b5596089d15d521102"),
                        Bytes.FromHexString("0x02f8620180011482520894475674cb523a0a2736b7f7534390288fce16982c0180c001a0033e85439a128c42f2ba47ca278f1375ef211e61750018ff21bcd9750d1893f2a04ee981fe5261f8853f95c865232ffdab009abcc7858ca051fb624c49744bf18d")
                    },
                },
                id = 67
            }));
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
        ulong timestamp = Timestamper.UnixTime.Seconds;
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

        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        List<int?> transactionsLength = improvementContextFactory.CreatedContexts
            .Select(c => c.CurrentBestBlock?.Transactions.Length).ToList();

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

        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        getPayloadResult.GetTransactions().Should().HaveCount(3);
        cancelledContext?.Disposed.Should().BeTrue();
    }

    [Test, Repeat(5000)]
    public async Task Cannot_produce_bad_blocks()
    {
        // this test sends two payloadAttributes on block X and X + 1 to start many block improvements
        // as the result we want to check if we are not able to produce invalid block by repeating this test many times
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockChain();
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 4 * delay;
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
        Keccak blockX = chain.BlockTree.HeadHash;
        chain.AddTransactions(BuildTransactions(chain, blockX, TestItem.PrivateKeyB, TestItem.AddressF, 3, 10, out _, out _));
        chain.PayloadPreparationService!.BlockImproved += (_, _) => { blockImprovementLock.Release(1); };
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(blockX, Keccak.Zero, blockX),
                new PayloadAttributes { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;
        chain.AddTransactions(BuildTransactions(chain, blockX, TestItem.PrivateKeyC, TestItem.AddressA, 3, 10, out _, out _));
        await blockImprovementLock.WaitAsync(timePerSlot * 2);
        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        chain.AddTransactions(BuildTransactions(chain, blockX, TestItem.PrivateKeyA, TestItem.AddressC, 5, 10, out _, out _));

        Task<ResultWrapper<PayloadStatusV1>> result1 = await rpc.engine_newPayloadV2(getPayloadResult);
        result1.Result.Data.Status.Should().Be(PayloadStatus.Valid);

        // starting building on block X
        await rpc.engine_forkchoiceUpdatedV1(
            new ForkchoiceStateV1(blockX, Keccak.Zero, blockX),
            new PayloadAttributes { Timestamp = 101, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero });

        int milliseconds = RandomNumberGenerator.GetInt32(timePerSlot.Milliseconds, timePerSlot.Milliseconds * 2);
        await Task.Delay(milliseconds);

        // starting building on block X + 1
        string? secondNewPayload = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(getPayloadResult.BlockHash, Keccak.Zero, getPayloadResult.BlockHash),
                new PayloadAttributes { Timestamp = 102, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;

        ExecutionPayload getSecondBlockPayload = (await rpc.engine_getPayloadV1(Bytes.FromHexString(secondNewPayload))).Data!;

        Task<ResultWrapper<PayloadStatusV1>> secondBlock = rpc.engine_newPayloadV2(getSecondBlockPayload);
        secondBlock.Result.Data.Status.Should().Be(PayloadStatus.Valid);
    }
}

