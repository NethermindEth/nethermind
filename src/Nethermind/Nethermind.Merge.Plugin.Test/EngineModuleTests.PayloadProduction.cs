// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task getPayloadV1_should_allow_asking_multiple_times_by_same_payload_id()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);

        Hash256 startingHead = chain.BlockTree.HeadHash;
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
    [Obsolete]
    public async Task getPayloadV1_should_return_error_if_called_after_cleanup_timer()
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        TimeSpan timePerSlot = TimeSpan.FromMilliseconds(10);
        using MergeTestBlockchain chain = await CreateBlockchainWithImprovementContext(
            static chain => new BlockImprovementContextFactory(chain.PostMergeBlockProducer!, TimeSpan.FromSeconds(1)),
            timePerSlot, mergeConfig);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
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
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        uint count = 3;
        int value = 10;
        Address recipient = TestItem.AddressF;
        PrivateKey sender = TestItem.PrivateKeyB;
        Transaction[] transactions = BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);
        chain.AddTransactions(transactions);
        chain.StoringBlockImprovementContextFactory!.BlockImproved += (_, _) => { blockImprovementLock.Release(1); };
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes() { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;

        await blockImprovementLock.WaitAsync(10000);
        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        getPayloadResult.StateRoot.Should().NotBe(chain.BlockTree.Genesis!.StateRoot!);

        Transaction[] transactionsInBlock = getPayloadResult.TryGetTransactions().Transactions;
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
            yield return new TestCaseData(TimeSpan.Zero, TimeSpan.Zero, 50, 50) { TestName = "Production manages to finish" };
            yield return new TestCaseData(TimeSpan.FromMilliseconds(10), PayloadPreparationService.GetPayloadWaitForNonEmptyBlockMillisecondsDelay / 4, 2, 5) { TestName = "Production makes partial block" };
            yield return new TestCaseData(TimeSpan.Zero, PayloadPreparationService.GetPayloadWaitForNonEmptyBlockMillisecondsDelay * 2, 0, 0) { TestName = "Production takes too long" };
        }
    }

    private class TxDelayedSource(Transaction[] transactions, TimeSpan delay) : ITxSource
    {
        public bool SupportsBlobs { get; }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes, bool filterSource)
        {
            foreach (var item in transactions)
            {
                if (delay.TotalMilliseconds > 0)
                    Thread.Sleep(delay);
                yield return item;
            }
        }
    }

    [TestCaseSource(nameof(WaitTestCases))]
    public async Task getPayloadV1_waits_for_block_production(TimeSpan txDelay, TimeSpan improveDelay, int minCount, int maxCount)
    {
        using MergeTestBlockchain chain = await CreateBlockchainWithImprovementContext(
            chain =>
            {
                Hash256 startingHead = chain.BlockTree.HeadHash;
                uint count = 50;
                int value = 10;
                Address recipient = TestItem.AddressF;
                PrivateKey sender = TestItem.PrivateKeyB;
                Transaction[] transactions = BuildTransactions(chain, startingHead, sender, recipient, count, value, out _, out _);
                chain.PostMergeBlockProducer!.TxSource = new TxDelayedSource(transactions, txDelay);
                return new DelayBlockImprovementContextFactory(chain.PostMergeBlockProducer, TimeSpan.FromSeconds(10), improveDelay);
            },
            TimeSpan.FromSeconds(10));

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;

        string payloadId = (await rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })).Data.PayloadId!;

        await Task.Delay(PayloadPreparationService.GetPayloadWaitForNonEmptyBlockMillisecondsDelay);

        Assert.That(() => rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId)).Result.Data!.Transactions,
            Has.Length.InRange(minCount, maxCount).After(2000, 1)); // Polling interval need to be short or it might miss it.
    }

    [Test]
    public async Task getPayloadV1_return_correct_block_values_for_empty_block()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Hash256? random = TestItem.KeccakF;
        ulong timestamp = chain.BlockTree.Head!.Timestamp + 5;
        Address? suggestedFeeRecipient = TestItem.AddressC;
        PayloadAttributes? payloadAttributes = new() { PrevRandao = random, Timestamp = timestamp, SuggestedFeeRecipient = suggestedFeeRecipient };
        ExecutionPayload getPayloadResult = await BuildAndGetPayloadResult(chain, rpc, payloadAttributes);
        getPayloadResult.ParentHash.Should().Be(startingHead);


        ResultWrapper<PayloadStatusV1> executePayloadResult = await rpc.engine_newPayloadV1(getPayloadResult);
        executePayloadResult.Data.Status.Should().Be(PayloadStatus.Valid);

        BlockHeader? currentHeader = chain.BlockTree.BestSuggestedHeader!;

        Assert.That(currentHeader.UnclesHash!.ToString(), Is.EqualTo("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"));
        Assert.That(currentHeader.Difficulty, Is.EqualTo((UInt256)0));
        Assert.That(currentHeader.Nonce, Is.EqualTo(0));
        Assert.That(currentHeader.MixHash, Is.EqualTo(random));
    }

    [Test]
    public async Task getPayloadV1_should_return_error_if_there_was_no_corresponding_prepare_call()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
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
        byte[] payload = [];
        IPayloadPreparationService payloadPreparationService = Substitute.For<IPayloadPreparationService>();
        Block block = Build.A.Block.WithTransactions(
            Build.A.Transaction.WithTo(TestItem.AddressD).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithTo(TestItem.AddressD).WithType(TxType.EIP1559).WithMaxFeePerGas(20).SignedAndResolved(TestItem.PrivateKeyA).TestObject).TestObject;
        IBlockProductionContext improvementContext = Substitute.For<IBlockProductionContext>();
        improvementContext.CurrentBestBlock.Returns(block);
        payloadPreparationService.GetPayload(Arg.Any<string>(), Arg.Any<bool>()).Returns(improvementContext);
        using MergeTestBlockchain chain = await CreateBlockchain(null, null, payloadPreparationService);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        string result = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", payload.ToHexString(true));
        Assert.That(chain.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            result = new ExecutionPayload
            {
                BaseFeePerGas = 0,
                BlockHash = new("0x28012c3a37c85b37f9dc6db2d874f9c92b5d8d4bb784177c5309a0c6d7af6ef4"),
                BlockNumber = 0,
                ExtraData = Bytes.FromHexString("0x010203"),
                FeeRecipient = Address.Zero,
                GasLimit = 0x3d0900L,
                GasUsed = 0,
                LogsBloom =
                        new Bloom(Bytes.FromHexString(
                            "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")),
                ParentHash = new("0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c"),
                PrevRandao = new("0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2"),
                ReceiptsRoot = new("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"),
                StateRoot = new("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"),
                Timestamp = 0xf4240UL,
                Transactions = new[]
                    {
                        Bytes.FromHexString(
                            "0xf85f800182520894475674cb523a0a2736b7f7534390288fce16982c018025a0634db2f18f24d740be29e03dd217eea5757ed7422680429bdd458c582721b6c2a02f0fa83931c9a99d3448a46b922261447d6a41d8a58992b5596089d15d521102"),
                        Bytes.FromHexString(
                            "0x02f8620180011482520894475674cb523a0a2736b7f7534390288fce16982c0180c001a0db002b398e038bc919b316a214154aa6d9d5e404cb201aa8a151efb92f9fdbbda07bee8ea6915ed54bb07af4cd69b201548fe9aac699978e5c444405dc49f55a36")
                    },
            },
            id = 67
        }), Is.EqualTo(result));
    }

    [Test]
    public async Task getPayload_should_serialize_unknown_payload_response_properly()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        byte[] payloadId = Bytes.FromHexString("0x1111111111111111");

        string parameters = payloadId.ToHexString(true);
        string result = await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV1", parameters);
        result.Should().Be("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-38001,\"message\":\"unknown payload\"},\"id\":67}");
    }

    [Test]
    [Retry(3)]
    [Obsolete]
    public async Task consecutive_blockImprovements_should_be_disposed()
    {
        MergeConfig mergeConfig = new() { SecondsPerSlot = 1, TerminalTotalDifficulty = "0" };
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 10 * delay;
        using MergeTestBlockchain chain = await CreateBlockchainWithImprovementContext(
            static chain => new StoringBlockImprovementContextFactory(new MockBlockImprovementContextFactory()),
            timePerSlot, mergeConfig, delay);
        StoringBlockImprovementContextFactory improvementContextFactory = chain.StoringBlockImprovementContextFactory!;

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ulong timestamp = Timestamper.UnixTime.Seconds;
        Hash256 random = Keccak.Zero;
        Address feeRecipient = Address.Zero;

        CancellationTokenSource cts = new();
        Task addingTx = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                Interlocked.Increment(ref TxPool.Metrics.PendingTransactionsAdded);
                await Task.Delay(10);
            }
        });

        Task waitForImprovement = chain.WaitForImprovedBlock();
        string payloadId = rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            new PayloadAttributes { Timestamp = timestamp, SuggestedFeeRecipient = feeRecipient, PrevRandao = random }).Result.Data.PayloadId!;
        await waitForImprovement;

        Assert.That(() => improvementContextFactory.CreatedContexts.Count, Is.InRange(3, 5).After(timePerSlot.Milliseconds * 10, 1));

        cts.Cancel();
        await addingTx;

        improvementContextFactory.CreatedContexts.Take(improvementContextFactory.CreatedContexts.Count - 1).Should().OnlyContain(static i => i.Disposed);

        await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));

        improvementContextFactory.CreatedContexts.Should().OnlyContain(static i => i.Disposed);
    }

    [Test, Retry(3)]
    public async Task getPayloadV1_picks_transactions_from_pool_constantly_improving_blocks()
    {
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 50 * delay;
        using MergeTestBlockchain chain = await CreateBlockchainWithImprovementContext(
            chain => new StoringBlockImprovementContextFactory(new BlockImprovementContextFactory(chain.PostMergeBlockProducer!, TimeSpan.FromSeconds(chain.MergeConfig.SecondsPerSlot))),
            timePerSlot, delay: delay);
        StoringBlockImprovementContextFactory improvementContextFactory = (StoringBlockImprovementContextFactory)chain.BlockImprovementContextFactory;

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Task improvementWaitTask = chain.WaitForImprovedBlock();
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyB, TestItem.AddressF, 3, 10, out _, out _));
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;
        await improvementWaitTask;

        improvementWaitTask = chain.WaitForImprovedBlock();
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyC, TestItem.AddressA, 3, 10, out _, out _));
        await improvementWaitTask;

        improvementWaitTask = chain.WaitForImprovedBlock();
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyA, TestItem.AddressC, 5, 10, out _, out _));
        await improvementWaitTask;

        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        List<int?> transactionsLength = improvementContextFactory.CreatedContexts
            .Select(c => c.CurrentBestBlock?.Transactions.Length).ToList();

        transactionsLength.Should().Equal(3, 6, 11);
        Transaction[] txs = getPayloadResult.TryGetTransactions().Transactions;

        txs.Should().HaveCount(11);
    }

    [Test, Retry(3)]
    public async Task TestTwoTransaction_SameContract_WithBlockImprovement()
    {
        string tx1Hex =
            "0x02f91adc83aa36a70184b2d05e00850e7e2cc28c831d6d968080b91a7f60806040523480156200001157600080fd5b5060405162001a5f38038062001a5f8339810160408190526200003491620000a1565b6200003f3362000051565b6200004a8162000051565b50620000d3565b600080546001600160a01b038381166001600160a01b0319831681178455604051919092169283917f8be0079c531659141344cd1fd0a4f28419497f9722a3daafe3b4186f6b6457e09190a35050565b600060208284031215620000b457600080fd5b81516001600160a01b0381168114620000cc57600080fd5b9392505050565b61197c80620000e36000396000f3fe60806040526004361061010e5760003560e01c8063860f7cda116100a557806399a88ec411610074578063b794726211610059578063b794726214610329578063f2fde38b14610364578063f3b7dead1461038457600080fd5b806399a88ec4146102e95780639b2ea4bd1461030957600080fd5b8063860f7cda1461026b5780638d52d4a01461028b5780638da5cb5b146102ab5780639623609d146102d657600080fd5b80633ab76e9f116100e15780633ab76e9f146101cc5780636bd9f516146101f9578063715018a6146102365780637eff275e1461024b57600080fd5b80630652b57a1461011357806307c8f7b014610135578063204e1c7a14610155578063238181ae1461019f575b600080fd5b34801561011f57600080fd5b5061013361012e3660046111f9565b6103a4565b005b34801561014157600080fd5b50610133610150366004611216565b6103f3565b34801561016157600080fd5b506101756101703660046111f9565b610445565b60405173ffffffffffffffffffffffffffffffffffffffff90911681526020015b60405180910390f35b3480156101ab57600080fd5b506101bf6101ba3660046111f9565b61066b565b60405161019691906112ae565b3480156101d857600080fd5b506003546101759073ffffffffffffffffffffffffffffffffffffffff1681565b34801561020557600080fd5b506102296102143660046111f9565b60016020526000908152604090205460ff1681565b60405161019691906112f0565b34801561024257600080fd5b50610133610705565b34801561025757600080fd5b50610133610266366004611331565b610719565b34801561027757600080fd5b5061013361028636600461148c565b6108cc565b34801561029757600080fd5b506101336102a63660046114dc565b610903565b3480156102b757600080fd5b5060005473ffffffffffffffffffffffffffffffffffffffff16610175565b6101336102e436600461150e565b610977565b3480156102f557600080fd5b50610133610304366004611331565b610b8e565b34801561031557600080fd5b50610133610324366004611584565b610e1e565b34801561033557600080fd5b5060035474010000000000000000000000000000000000000000900460ff166040519015158152602001610196565b34801561037057600080fd5b5061013361037f3660046111f9565b610eb4565b34801561039057600080fd5b5061017561039f3660046111f9565b610f6b565b6103ac6110e1565b600380547fffffffffffffffffffffffff00000000000000000000000000000000000000001673ffffffffffffffffffffffffffffffffffffffff92909216919091179055565b6103fb6110e1565b6003805491151574010000000000000000000000000000000000000000027fffffffffffffffffffffff00ffffffffffffffffffffffffffffffffffffffff909216919091179055565b73ffffffffffffffffffffffffffffffffffffffff811660009081526001602052604081205460ff1681816002811115610481576104816112c1565b036104fc578273ffffffffffffffffffffffffffffffffffffffff16635c60da1b6040518163ffffffff1660e01b8152600401602060405180830381865afa1580156104d1573d6000803e3d6000fd5b505050506040513d601f19601f820116820180604052508101906104f591906115cb565b9392505050565b6001816002811115610510576105106112c1565b03610560578273ffffffffffffffffffffffffffffffffffffffff1663aaf10f426040518163ffffffff1660e01b8152600401602060405180830381865afa1580156104d1573d6000803e3d6000fd5b6002816002811115610574576105746112c1565b036105fe5760035473ffffffffffffffffffffffffffffffffffffffff8481166000908152600260205260409081902090517fbf40fac1000000000000000000000000000000000000000000000000000000008152919092169163bf40fac1916105e19190600401611635565b602060405180830381865afa1580156104d1573d6000803e3d6000fd5b6040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152601e60248201527f50726f787941646d696e3a20756e6b6e6f776e2070726f78792074797065000060448201526064015b60405180910390fd5b50919050565b60026020526000908152604090208054610684906115e8565b80601f01602080910402602001604051908101604052809291908181526020018280546106b0906115e8565b80156106fd5780601f106106d2576101008083540402835291602001916106fd565b820191906000526020600020905b8154815290600101906020018083116106e057829003601f168201915b505050505081565b61070d6110e1565b6107176000611162565b565b6107216110e1565b73ffffffffffffffffffffffffffffffffffffffff821660009081526001602052604081205460ff169081600281111561075d5761075d6112c1565b036107e9576040517f8f28397000000000000000000000000000000000000000000000000000000000815273ffffffffffffffffffffffffffffffffffffffff8381166004830152841690638f283970906024015b600060405180830381600087803b1580156107cc57600080fd5b505af11580156107e0573d6000803e3d6000fd5b50505050505050565b60018160028111156107fd576107fd6112c1565b03610856576040517f13af403500000000000000000000000000000000000000000000000000000000815273ffffffffffffffffffffffffffffffffffffffff83811660048301528416906313af4035906024016107b2565b600281600281111561086a5761086a6112c1565b036105fe576003546040517ff2fde38b00000000000000000000000000000000000000000000000000000000815273ffffffffffffffffffffffffffffffffffffffff84811660048301529091169063f2fde38b906024016107b2565b505050565b6108d46110e1565b73ffffffffffffffffffffffffffffffffffffffff821660009081526002602052604090206108c78282611724565b61090b6110e1565b73ffffffffffffffffffffffffffffffffffffffff82166000908152600160208190526040909120805483927fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff009091169083600281111561096e5761096e6112c1565b02179055505050565b61097f6110e1565b73ffffffffffffffffffffffffffffffffffffffff831660009081526001602052604081205460ff16908160028111156109bb576109bb6112c1565b03610a81576040517f4f1ef28600000000000000000000000000000000000000000000000000000000815273ffffffffffffffffffffffffffffffffffffffff851690634f1ef286903490610a16908790879060040161183e565b60006040518083038185885af1158015610a34573d6000803e3d6000fd5b50505050506040513d6000823e601f3d9081017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe0168201604052610a7b9190810190611875565b50610b88565b610a8b8484610b8e565b60008473ffffffffffffffffffffffffffffffffffffffff163484604051610ab391906118ec565b60006040518083038185875af1925050503d8060008114610af0576040519150601f19603f3d011682016040523d82523d6000602084013e610af5565b606091505b5050905080610b86576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152602e60248201527f50726f787941646d696e3a2063616c6c20746f2070726f78792061667465722060448201527f75706772616465206661696c6564000000000000000000000000000000000000606482015260840161065c565b505b50505050565b610b966110e1565b73ffffffffffffffffffffffffffffffffffffffff821660009081526001602052604081205460ff1690816002811115610bd257610bd26112c1565b03610c2b576040517f3659cfe600000000000000000000000000000000000000000000000000000000815273ffffffffffffffffffffffffffffffffffffffff8381166004830152841690633659cfe6906024016107b2565b6001816002811115610c3f57610c3f6112c1565b03610cbe576040517f9b0b0fda0000000000000000000000000000000000000000000000000000000081527f360894a13ba1a3210667c828492db98dca3e2076cc3735a920a3ca505d382bbc600482015273ffffffffffffffffffffffffffffffffffffffff8381166024830152841690639b0b0fda906044016107b2565b6002816002811115610cd257610cd26112c1565b03610e165773ffffffffffffffffffffffffffffffffffffffff831660009081526002602052604081208054610d07906115e8565b80601f0160208091040260200160405190810160405280929190818152602001828054610d33906115e8565b8015610d805780601f10610d5557610100808354040283529160200191610d80565b820191906000526020600020905b815481529060010190602001808311610d6357829003601f168201915b50506003546040517f9b2ea4bd00000000000000000000000000000000000000000000000000000000815294955073ffffffffffffffffffffffffffffffffffffffff1693639b2ea4bd9350610dde92508591508790600401611908565b600060405180830381600087803b158015610df857600080fd5b505af1158015610e0c573d6000803e3d6000fd5b5050505050505050565b6108c7611940565b610e266110e1565b6003546040517f9b2ea4bd00000000000000000000000000000000000000000000000000000000815273ffffffffffffffffffffffffffffffffffffffff90911690639b2ea4bd90610e7e9085908590600401611908565b600060405180830381600087803b158015610e9857600080fd5b505af1158015610eac573d6000803e3d6000fd5b505050505050565b610ebc6110e1565b73ffffffffffffffffffffffffffffffffffffffff8116610f5f576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152602660248201527f4f776e61626c653a206e6577206f776e657220697320746865207a65726f206160448201527f6464726573730000000000000000000000000000000000000000000000000000606482015260840161065c565b610f6881611162565b50565b73ffffffffffffffffffffffffffffffffffffffff811660009081526001602052604081205460ff1681816002811115610fa757610fa76112c1565b03610ff7578273ffffffffffffffffffffffffffffffffffffffff1663f851a4406040518163ffffffff1660e01b8152600401602060405180830381865afa1580156104d1573d6000803e3d6000fd5b600181600281111561100b5761100b6112c1565b0361105b578273ffffffffffffffffffffffffffffffffffffffff1663893d20e86040518163ffffffff1660e01b8152600401602060405180830381865afa1580156104d1573d6000803e3d6000fd5b600281600281111561106f5761106f6112c1565b036105fe57600360009054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16638da5cb5b6040518163ffffffff1660e01b8152600401602060405180830381865afa1580156104d1573d6000803e3d6000fd5b60005473ffffffffffffffffffffffffffffffffffffffff163314610717576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820181905260248201527f4f776e61626c653a2063616c6c6572206973206e6f7420746865206f776e6572604482015260640161065c565b6000805473ffffffffffffffffffffffffffffffffffffffff8381167fffffffffffffffffffffffff0000000000000000000000000000000000000000831681178455604051919092169283917f8be0079c531659141344cd1fd0a4f28419497f9722a3daafe3b4186f6b6457e09190a35050565b73ffffffffffffffffffffffffffffffffffffffff81168114610f6857600080fd5b60006020828403121561120b57600080fd5b81356104f5816111d7565b60006020828403121561122857600080fd5b813580151581146104f557600080fd5b60005b8381101561125357818101518382015260200161123b565b83811115610b885750506000910152565b6000815180845261127c816020860160208601611238565b601f017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe0169290920160200192915050565b6020815260006104f56020830184611264565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052602160045260246000fd5b602081016003831061132b577f4e487b7100000000000000000000000000000000000000000000000000000000600052602160045260246000fd5b91905290565b6000806040838503121561134457600080fd5b823561134f816111d7565b9150602083013561135f816111d7565b809150509250929050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052604160045260246000fd5b604051601f82017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe016810167ffffffffffffffff811182821017156113e0576113e061136a565b604052919050565b600067ffffffffffffffff8211156114025761140261136a565b50601f017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe01660200190565b600061144161143c846113e8565b611399565b905082815283838301111561145557600080fd5b828260208301376000602084830101529392505050565b600082601f83011261147d57600080fd5b6104f58383356020850161142e565b6000806040838503121561149f57600080fd5b82356114aa816111d7565b9150602083013567ffffffffffffffff8111156114c657600080fd5b6114d28582860161146c565b9150509250929050565b600080604083850312156114ef57600080fd5b82356114fa816111d7565b915060208301356003811061135f57600080fd5b60008060006060848603121561152357600080fd5b833561152e816111d7565b9250602084013561153e816111d7565b9150604084013567ffffffffffffffff81111561155a57600080fd5b8401601f8101861361156b57600080fd5b61157a8682356020840161142e565b9150509250925092565b6000806040838503121561159757600080fd5b823567ffffffffffffffff8111156115ae57600080fd5b6115ba8582860161146c565b925050602083013561135f816111d7565b6000602082840312156115dd57600080fd5b81516104f5816111d7565b600181811c908216806115fc57607f821691505b602082108103610665577f4e487b7100000000000000000000000000000000000000000000000000000000600052602260045260246000fd5b6000602080835260008454611649816115e8565b8084870152604060018084166000811461166a57600181146116a2576116d0565b7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff008516838a01528284151560051b8a010195506116d0565b896000528660002060005b858110156116c85781548b82018601529083019088016116ad565b8a0184019650505b509398975050505050505050565b601f8211156108c757600081815260208120601f850160051c810160208610156117055750805b601f850160051c820191505b81811015610eac57828155600101611711565b815167ffffffffffffffff81111561173e5761173e61136a565b6117528161174c84546115e8565b846116de565b602080601f8311600181146117a5576000841561176f5750858301515b7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff600386901b1c1916600185901b178555610eac565b6000858152602081207fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe08616915b828110156117f2578886015182559484019460019091019084016117d3565b508582101561182e57878501517fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff600388901b60f8161c191681555b5050505050600190811b01905550565b73ffffffffffffffffffffffffffffffffffffffff8316815260406020820152600061186d6040830184611264565b949350505050565b60006020828403121561188757600080fd5b815167ffffffffffffffff81111561189e57600080fd5b8201601f810184136118af57600080fd5b80516118bd61143c826113e8565b8181528560208385010111156118d257600080fd5b6118e3826020830160208601611238565b95945050505050565b600082516118fe818460208701611238565b9190910192915050565b60408152600061191b6040830185611264565b905073ffffffffffffffffffffffffffffffffffffffff831660208301529392505050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052600160045260246000fdfea164736f6c634300080f000a000000000000000000000000bc2fd1637c49839adb7bb57f9851eae3194a90f7c001a0679d6e22b738ede7fc2b3e93b95719539f297f4c7a9a2e4f5ad93b479254f3daa0595015bcc1435e0872b59a2a8f697322aa5cea9c40cdacf4e8d664d96cd1f59b";
        Transaction tx1 = TxDecoder.Instance.Decode(new RlpStream(Bytes.FromHexString(tx1Hex)), RlpBehaviors.SkipTypedWrapping)!;

        string tx2Hex =
            "0x02f89383aa36a70284b2d05e00850e7e2cc28c830106fc94785ea063ece4493f7995da4f9ef3661cac2da9c380a40652b57a0000000000000000000000008d1b673b7db916f3d9a59bbf997dda34ea69243ac001a0e6a2c179857e74052fc12a4441f317671bc4fbdda56f6466d2fa1a190b7cf326a05434bec9eb23531ce0186a64b1e7fca6ef13486c0b8196a52762bf48dc3ed798";
        Transaction tx2 = TxDecoder.Instance.Decode(new RlpStream(Bytes.FromHexString(tx2Hex)), RlpBehaviors.SkipTypedWrapping)!;

        MergeTestBlockchain blockchain = CreateBaseBlockchain(logManager: LimboLogs.Instance);
        blockchain.InitialStateMutator = state =>
        {
            state.CreateAccount(new Address("0xBC2Fd1637C49839aDB7Bb57F9851EAE3194A90f7"), (UInt256)1200482917041833040, 1);
        };
        using MergeTestBlockchain chain = await blockchain.Build(SepoliaSpecProvider.Instance);

        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 1000 * delay;
        StoringBlockImprovementContextFactory improvementContextFactory = new(
            new BlockImprovementContextFactory(chain.PostMergeBlockProducer!, TimeSpan.FromSeconds(chain.MergeConfig.SecondsPerSlot)),
            skipDuplicatedContext: true
        );
        ConfigureBlockchainWithImprovementContextFactory(chain, improvementContextFactory, timePerSlot, delay);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        chain.AddTransactions(tx1);

        Task improvedBlockWait = chain.WaitForImprovedBlock();
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;
        await improvedBlockWait;

        improvedBlockWait = chain.WaitForImprovedBlock();
        chain.AddTransactions(tx2);
        await improvedBlockWait;

        List<int?> transactionsLength = improvementContextFactory.CreatedContexts
            .Select(c => c.CurrentBestBlock?.Transactions.Length).ToList();

        transactionsLength.Should().Equal(1, 2);
        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;
        Transaction[] txs = getPayloadResult.TryGetTransactions().Transactions;

        txs.Should().HaveCount(2);
    }

    [Test]
    [Retry(3)]
    public async Task getPayloadV1_doesnt_wait_for_improvement_when_block_is_not_empty()
    {
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 50 * delay;
        using MergeTestBlockchain chain = await CreateBlockchainWithImprovementContext(
            chain => new StoringBlockImprovementContextFactory(new DelayBlockImprovementContextFactory(
                chain.PostMergeBlockProducer!, TimeSpan.FromSeconds(chain.MergeConfig.SecondsPerSlot), 3 * delay)),
            timePerSlot, delay: delay);
        StoringBlockImprovementContextFactory improvementContextFactory = (StoringBlockImprovementContextFactory)chain.BlockImprovementContextFactory;

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 startingHead = chain.BlockTree.HeadHash;
        Task blockImprovement = chain.WaitForImprovedBlock();
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyB, TestItem.AddressF, 3, 10, out _, out _));
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
                new PayloadAttributes { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;

        await blockImprovement;
        chain.AddTransactions(BuildTransactions(chain, startingHead, TestItem.PrivateKeyC, TestItem.AddressA, 3, 10, out _, out _));

        IBlockImprovementContext? cancelledContext = null;
        await Wait.ForEventCondition<ImprovementStartedEventArgs>(chain.CancellationToken,
            e => improvementContextFactory!.ImprovementStarted += e,
            e => improvementContextFactory!.ImprovementStarted -= e,
            e =>
            {
                cancelledContext = e.BlockImprovementContext;
                return true;
            });

        improvementContextFactory.CreatedContexts.Should().HaveCount(2);

        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;

        getPayloadResult.TryGetTransactions().Transactions.Should().HaveCount(3);
        cancelledContext?.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task Cannot_build_invalid_block_with_the_branch()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(new TestSingleReleaseSpecProvider(London.Instance));
        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 4 * delay;
        ConfigureBlockchainWithImprovementContextFactory(chain,
            new StoringBlockImprovementContextFactory(new BlockImprovementContextFactory(chain.PostMergeBlockProducer!, TimeSpan.FromSeconds(chain.MergeConfig.SecondsPerSlot))),
            timePerSlot, delay);

        IEngineRpcModule rpc = CreateEngineModule(chain);

        // creating chain with 30 blocks
        Task blockImprovementWait = chain.WaitForImprovedBlock();
        await ProduceBranchV1(rpc, chain, 30, CreateParentBlockRequestOnHead(chain.BlockTree), true);

        Block block30 = chain.BlockTree.Head!;

        // we added transactions
        chain.AddTransactions(BuildTransactions(chain, block30.CalculateHash(), TestItem.PrivateKeyB, TestItem.AddressF, 3, 10, out _, out _));
        PayloadAttributes payloadAttributesBlock31A = new PayloadAttributes
        {
            Timestamp = (ulong)DateTime.UtcNow.AddDays(3).Ticks,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero
        };
        string? payloadIdBlock31A = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(block30.GetOrCalculateHash(), Keccak.Zero, block30.GetOrCalculateHash()),
                payloadAttributesBlock31A)
            .Result.Data.PayloadId!;
        await blockImprovementWait;

        ExecutionPayload getPayloadResultBlock31A = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadIdBlock31A))).Data!;
        getPayloadResultBlock31A.Should().NotBeNull();
        await rpc.engine_newPayloadV1(getPayloadResultBlock31A);

        // current main chain block 30->31A, we start building payload 32A
        await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(getPayloadResultBlock31A.BlockHash, Keccak.Zero, getPayloadResultBlock31A.BlockHash));
        Block block31A = chain.BlockTree.Head!;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = (ulong)DateTime.UtcNow.AddDays(4).Ticks,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero
        };

        // we build one more block on the same level
        Block block31B = chain.PostMergeBlockProducer!.PrepareEmptyBlock(block30.Header, payloadAttributes);
        await rpc.engine_newPayloadV1(ExecutionPayload.Create(block31B));

        // ...and we change the main chain, so main chain now is 30->31B, block improvement for block 32A is still in progress
        string? payloadId = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(block31A.GetOrCalculateHash(), Keccak.Zero, block31A.GetOrCalculateHash()),
                payloadAttributes)
            .Result.Data.PayloadId!;
        ForkchoiceUpdatedV1Result result = rpc.engine_forkchoiceUpdatedV1(
            new ForkchoiceStateV1(block31B.GetOrCalculateHash(), Keccak.Zero, block31B.GetOrCalculateHash())).Result.Data;

        // we added same transactions, so if we build on incorrect state root, we will end up with invalid block
        blockImprovementWait = chain.WaitForImprovedBlock();
        chain.AddTransactions(BuildTransactions(chain, block31A.GetOrCalculateHash(), TestItem.PrivateKeyC, TestItem.AddressF, 3, 10, out _, out _));
        await blockImprovementWait;
        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;
        getPayloadResult.Should().NotBeNull();

        ResultWrapper<PayloadStatusV1> finalResult = await rpc.engine_newPayloadV1(getPayloadResult);
        finalResult.Data.Status.Should().Be(PayloadStatus.Valid);
    }

    [Test, Repeat(100)]
    public async Task Cannot_produce_bad_blocks()
    {
        // this test sends two payloadAttributes on block X and X + 1 to start many block improvements
        // as the result we want to check if we are not able to produce invalid block by repeating this test many times

        bool logInvalidBlockExecution = false; // change to true if you want to log invalid blocks
        string logFolder = "D:\\logs"; // adjust to your folder if needed by default logging is turned off
        string guid = Guid.NewGuid().ToString();
        ILogManager? logManager = LimboLogs.Instance;
        if (logInvalidBlockExecution)
        {
            logManager = new NLogManager($"log_+{guid}", logFolder);
        }

        TimeSpan delay = TimeSpan.FromMilliseconds(10);
        TimeSpan timePerSlot = 4 * delay;
        using MergeTestBlockchain chain = await CreateBlockchainWithImprovementContext(
            (chain) => new StoringBlockImprovementContextFactory(new BlockImprovementContextFactory(chain.PostMergeBlockProducer!, TimeSpan.FromSeconds(chain.MergeConfig.SecondsPerSlot))),
            timePerSlot, delay: delay);

        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 blockX = chain.BlockTree.HeadHash;
        chain.AddTransactions(BuildTransactions(chain, blockX, TestItem.PrivateKeyB, TestItem.AddressF, 3, 10, out _, out _));

        Task improvementTask = chain.WaitForImprovedBlock(blockX);

        string? payloadId = (await rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(blockX, Keccak.Zero, blockX),
                new PayloadAttributes { Timestamp = (ulong)DateTime.UtcNow.AddDays(3).Ticks, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero }))
                .Data.PayloadId!;
        chain.AddTransactions(BuildTransactions(chain, blockX, TestItem.PrivateKeyC, TestItem.AddressA, 3, 10, out _, out _));

        ExecutionPayload getPayloadResult = (await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId))).Data!;
        getPayloadResult.Should().NotBeNull();
        chain.AddTransactions(BuildTransactions(chain, blockX, TestItem.PrivateKeyA, TestItem.AddressC, 5, 10, out _, out _));

        await improvementTask;
        Task<ResultWrapper<PayloadStatusV1>> result1 = await rpc.engine_newPayloadV1(getPayloadResult);
        if (result1.Result.Data.Status != PayloadStatus.Valid)
        {
            string[] files = Directory.GetFiles(logFolder);
            foreach (string file in files)
            {
                if (!file.Contains(guid))
                    File.Delete(file);
            }
        }

        result1.Result.Data.Status.Should().Be(PayloadStatus.Valid);


        // starting building on block X
        await rpc.engine_forkchoiceUpdatedV1(
            new ForkchoiceStateV1(blockX, Keccak.Zero, blockX),
            new PayloadAttributes { Timestamp = (ulong)DateTime.UtcNow.AddDays(4).Ticks, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero });

        int milliseconds = RandomNumberGenerator.GetInt32(timePerSlot.Milliseconds, timePerSlot.Milliseconds * 2);
        await Task.Delay(milliseconds);

        // starting building on block X + 1
        string? secondNewPayload = rpc.engine_forkchoiceUpdatedV1(
                new ForkchoiceStateV1(getPayloadResult.BlockHash, Keccak.Zero, getPayloadResult.BlockHash),
                new PayloadAttributes { Timestamp = (ulong)DateTime.UtcNow.AddDays(5).Ticks, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero })
            .Result.Data.PayloadId!;

        ExecutionPayload getSecondBlockPayload = (await rpc.engine_getPayloadV1(Bytes.FromHexString(secondNewPayload))).Data!;

        Task<ResultWrapper<PayloadStatusV1>> secondBlock = rpc.engine_newPayloadV1(getSecondBlockPayload);
        if (secondBlock.Result.Data.Status != PayloadStatus.Valid)
        {
            string[] files = Directory.GetFiles(logFolder);
            foreach (string file in files)
            {
                if (!file.Contains(guid))
                    File.Delete(file);
            }
        }

        secondBlock.Result.Data.Status.Should().Be(PayloadStatus.Valid);
    }

    [Test]
    public async Task Empty_block_is_valid_V1()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockchain(new TestSingleReleaseSpecProvider(London.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 blockX = chain.BlockTree.HeadHash;
        await rpc.engine_forkchoiceUpdatedV1(
            new ForkchoiceStateV1(blockX, Keccak.Zero, blockX));

        PostMergeBlockProducer blockProducer = chain.PostMergeBlockProducer!;
        Block emptyBlock = blockProducer.PrepareEmptyBlock(chain.BlockTree.Head!.Header, new PayloadAttributes { Timestamp = (ulong)DateTime.UtcNow.AddDays(5).Ticks, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = Address.Zero });
        Task<ResultWrapper<PayloadStatusV1>> result1 = await rpc.engine_newPayloadV1(ExecutionPayload.Create(emptyBlock));
        result1.Result.Data.Status.Should().Be(PayloadStatus.Valid);
    }

    [Test]
    public virtual async Task Empty_block_is_valid_with_withdrawals_V2()
    {
        using SemaphoreSlim blockImprovementLock = new(0);
        using MergeTestBlockchain chain = await CreateBlockchain(new TestSingleReleaseSpecProvider(Shanghai.Instance));
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Hash256 blockX = chain.BlockTree.HeadHash;
        await rpc.engine_forkchoiceUpdatedV2(
            new ForkchoiceStateV1(blockX, Keccak.Zero, blockX));

        PostMergeBlockProducer blockProducer = chain.PostMergeBlockProducer!;
        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = (ulong)DateTime.UtcNow.AddDays(5).Ticks,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = [TestItem.WithdrawalA_1Eth]
        };
        Block emptyBlock = blockProducer.PrepareEmptyBlock(chain.BlockTree.Head!.Header, payloadAttributes);
        Task<ResultWrapper<PayloadStatusV1>> result1 = await rpc.engine_newPayloadV2(ExecutionPayload.Create(emptyBlock));
        result1.Result.Data.Status.Should().Be(PayloadStatus.Valid);
    }

    private async Task<MergeTestBlockchain> CreateBlockchainWithImprovementContext(
        Func<MergeTestBlockchain, IBlockImprovementContextFactory> factoryFactory,
        TimeSpan timePerSlot,
        IMergeConfig? mergeConfig = null,
        TimeSpan? delay = null
    )
    {
        MergeTestBlockchain chain = await CreateBlockchain(null, mergeConfig);
        IBlockImprovementContextFactory improvementContextFactory = factoryFactory(chain);
        ConfigureBlockchainWithImprovementContextFactory(chain, improvementContextFactory, timePerSlot, delay);
        return chain;
    }

    private void ConfigureBlockchainWithImprovementContextFactory(
        MergeTestBlockchain chain,
        IBlockImprovementContextFactory blockImprovementContext,
        TimeSpan timePerSlot,
        TimeSpan? delay = null
    )
    {
        chain.BlockImprovementContextFactory = blockImprovementContext;
        chain.PayloadPreparationService = new PayloadPreparationService(
            chain.PostMergeBlockProducer!,
            chain.BlockImprovementContextFactory,
            TimerFactory.Default,
            chain.LogManager,
            timePerSlot,
            improvementDelay: delay);
    }
}
