// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test;

public class TestingRpcModuleTests
{
    [Test]
    public async Task Sets_excess_blob_gas_and_withdrawals_root()
    {
        Hash256? suggestedWithdrawalsRoot = null;
        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateDefaultTestingModule(
            onProcess: block => suggestedWithdrawalsRoot = block.Header.WithdrawalsRoot);

        PayloadAttributes payloadAttributes = CreateDefaultPayloadAttributes(parentHeader,
            withdrawals: [new Withdrawal { Index = 0, ValidatorIndex = 0, Address = Address.Zero, AmountInGwei = 1 }]);

        ResultWrapper<object?> result = await module.testing_buildBlockV1(parentHash, payloadAttributes, Array.Empty<byte[]>(), Array.Empty<byte>());

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Should().BeOfType<GetPayloadV5Result>();
        GetPayloadV5Result payloadResult = (GetPayloadV5Result)result.Data!;
        payloadResult.ExecutionPayload.BlobGasUsed.Should().Be(0);
        payloadResult.ExecutionPayload.ExcessBlobGas.Should().Be(BlobGasCalculator.CalculateExcessBlobGas(parentHeader, Osaka.Instance));
        suggestedWithdrawalsRoot.Should().Be(new WithdrawalTrie(payloadAttributes.Withdrawals!).RootHash);
    }

    [Test]
    public async Task Json_rpc_accepts_omitted_extraData()
    {
        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateDefaultTestingModule();

        JsonRpcResponse response = await RpcTest.TestRequest<ITestingRpcModule>(
            module,
            nameof(ITestingRpcModule.testing_buildBlockV1),
            parentHash,
            CreateDefaultPayloadAttributes(parentHeader),
            Array.Empty<byte[]>());

        response.Should().BeOfType<JsonRpcSuccessResponse>();
    }

    [TestCaseSource(nameof(BuildBlockV1ForkCases))]
    public async Task Returns_fork_specific_payload(
        IReleaseSpec spec,
        bool expectsBlockAccessList,
        bool expectsSlotNumber)
    {
        ulong? parentSlot = expectsSlotNumber ? 1UL : null;
        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateDefaultTestingModule(
            spec: spec, slotNumber: parentSlot);

        Transaction[] transactions = BuildSignedTransactions(2);
        byte[][] txRlps = EncodeTransactions(transactions, out string[] txHex);

        ulong? slotNumber = expectsSlotNumber ? parentSlot!.Value + 1 : null;
        PayloadAttributes payloadAttributes = CreateDefaultPayloadAttributes(parentHeader, slotNumber: slotNumber);

        string json = await RpcTest.TestSerializedRequest<ITestingRpcModule>(
            module,
            nameof(ITestingRpcModule.testing_buildBlockV1),
            parentHash,
            payloadAttributes,
            txRlps,
            Array.Empty<byte>());

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        JsonElement executionPayload = root.GetProperty("result").GetProperty("executionPayload");
        JsonElement transactionsJson = executionPayload.GetProperty("transactions");

        transactionsJson.GetArrayLength().Should().Be(txHex.Length);
        for (int i = 0; i < txHex.Length; i++)
        {
            transactionsJson[i].GetString().Should().Be(txHex[i]);
        }

        if (expectsBlockAccessList)
        {
            executionPayload.TryGetProperty("blockAccessList", out JsonElement blockAccessList).Should().BeTrue();
            blockAccessList.GetString().Should().NotBeNullOrEmpty();
        }
        else
        {
            executionPayload.TryGetProperty("blockAccessList", out _).Should().BeFalse();
        }

        if (expectsSlotNumber)
        {
            executionPayload.TryGetProperty("slotNumber", out JsonElement slotNumberJson).Should().BeTrue();
            slotNumberJson.GetString().Should().Be(slotNumber!.Value.ToHexString(skipLeadingZeros: true));
        }
        else
        {
            executionPayload.TryGetProperty("slotNumber", out _).Should().BeFalse();
        }
    }

    public static IEnumerable<TestCaseData> BuildBlockV1ForkCases()
    {
        yield return new TestCaseData(Osaka.Instance, false, false)
            .SetName("Returns_fork_specific_payload_for_fusaka");
        yield return new TestCaseData(Amsterdam.Instance, true, true)
            .SetName("Returns_fork_specific_payload_for_amsterdam");
    }

    [TestCase(true, 1, Description = "null txRlps uses mempool")]
    [TestCase(false, 0, Description = "empty txRlps builds empty block")]
    public async Task Tx_source_behavior(bool useNull, int expectedTxCount)
    {
        Transaction mempoolTx = BuildSignedTransactions(1)[0];
        ITxSource txSource = Substitute.For<ITxSource>();
        txSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns(new[] { mempoolTx });

        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateDefaultTestingModule(txSource: txSource);

        byte[][]? txRlps = useNull ? null : Array.Empty<byte[]>();
        ResultWrapper<object?> result = await module.testing_buildBlockV1(
            parentHash, CreateDefaultPayloadAttributes(parentHeader), txRlps, Array.Empty<byte>());

        result.Result.ResultType.Should().Be(ResultType.Success);
        ((GetPayloadV5Result)result.Data!).ExecutionPayload.Transactions.Should().HaveCount(expectedTxCount);

        if (useNull)
            txSource.Received(1).GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>(), Arg.Any<PayloadAttributes>(), true);
        else
            txSource.DidNotReceive().GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>());
    }

    [Test]
    public async Task Fails_when_transaction_is_invalid()
    {
        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateDefaultTestingModule(
            processOverride: block =>
            {
                Block processedBlock = new(block.Header, [block.Transactions[0]], Array.Empty<BlockHeader>(), block.Withdrawals);
                processedBlock.Header.StateRoot ??= Keccak.EmptyTreeHash;
                processedBlock.Header.ReceiptsRoot ??= Keccak.EmptyTreeHash;
                processedBlock.Header.Bloom ??= Bloom.Empty;
                processedBlock.Header.GasUsed = 21_000;
                processedBlock.Header.Hash ??= Keccak.Compute("produced");
                return processedBlock;
            });

        byte[][] txRlps = EncodeTransactions(BuildSignedTransactions(2), out _);

        ResultWrapper<object?> result = await module.testing_buildBlockV1(
            parentHash, CreateDefaultPayloadAttributes(parentHeader), txRlps, Array.Empty<byte>());

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("expected 2 transactions but only 1 were included");
    }

    [Test]
    public async Task Returns_error_for_unknown_parent()
    {
        (TestingRpcModule module, _, BlockHeader parentHeader) = CreateDefaultTestingModule();

        Hash256 unknownHash = Keccak.Compute("unknown");
        ResultWrapper<object?> result = await module.testing_buildBlockV1(
            unknownHash, CreateDefaultPayloadAttributes(parentHeader), null);

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("unknown parent block");
    }

    private static (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) CreateDefaultTestingModule(
        IReleaseSpec? spec = null,
        ulong? slotNumber = null,
        Action<Block>? onProcess = null,
        Func<Block, Block?>? processOverride = null,
        ITxSource? txSource = null)
    {
        Hash256 parentHash = Keccak.Compute("parent");
        BlockHeader parentHeader = new(
            Keccak.Compute("grandparent"),
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.Zero,
            1,
            30_000_000,
            1,
            [])
        {
            Hash = parentHash,
            TotalDifficulty = UInt256.Zero,
            BaseFeePerGas = UInt256.One,
            GasUsed = 0,
            StateRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            Bloom = Bloom.Empty,
            BlobGasUsed = 0,
            ExcessBlobGas = 0,
            SlotNumber = slotNumber
        };
        Block parentBlock = new(parentHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), Array.Empty<Withdrawal>());

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec ?? Osaka.Instance);

        IGasLimitCalculator gasLimitCalculator = Substitute.For<IGasLimitCalculator>();
        gasLimitCalculator.GetGasLimit(Arg.Any<BlockHeader>()).Returns(parentHeader.GasLimit);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(parentHash).Returns(parentBlock);

        IBlockchainProcessor blockchainProcessor = CreateBlockProcessor(processOverride, onProcess);

        IBlockProducerEnv blockProducerEnv = Substitute.For<IBlockProducerEnv>();
        blockProducerEnv.ChainProcessor.Returns(blockchainProcessor);
        if (txSource is not null)
            blockProducerEnv.TxSource.Returns(txSource);

        IBlockProducerEnvFactory blockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>();
        blockProducerEnvFactory.CreateTransient().Returns(new ScopedBlockProducerEnv(blockProducerEnv, Substitute.For<IAsyncDisposable>()));

        TestingRpcModule module = new(blockProducerEnvFactory, gasLimitCalculator, specProvider, blockFinder, LimboLogs.Instance);
        return (module, parentHash, parentHeader);
    }

    private static IBlockchainProcessor CreateBlockProcessor(
        Func<Block, Block?>? processOverride = null,
        Action<Block>? onProcess = null)
    {
        IBlockchainProcessor processor = Substitute.For<IBlockchainProcessor>();

        if (processOverride is not null)
        {
            processor
                .Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => processOverride(callInfo.Arg<Block>()));
        }
        else
        {
            processor
                .Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>())
                .Returns(static callInfo =>
                {
                    Block block = callInfo.Arg<Block>();
                    block.Header.StateRoot ??= Keccak.EmptyTreeHash;
                    block.Header.ReceiptsRoot ??= Keccak.EmptyTreeHash;
                    block.Header.Bloom ??= Bloom.Empty;
                    block.Header.GasUsed = 0;
                    block.Header.Hash ??= Keccak.Compute("produced");

                    if (block.Header.SlotNumber is not null && block.BlockAccessList is null && block.Header.ParentHash is not null)
                    {
                        block.BlockAccessList = Nethermind.Core.Test.Builders.Build.A.BlockAccessList.WithPrecompileChanges(block.Header.ParentHash, block.Header.Timestamp).TestObject;
                    }

                    return block;
                });
        }

        if (onProcess is not null)
        {
            processor
                .When(x => x.Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>()))
                .Do(callInfo => onProcess(callInfo.Arg<Block>()));
        }

        return processor;
    }

    private static PayloadAttributes CreateDefaultPayloadAttributes(
        BlockHeader parentHeader,
        Withdrawal[]? withdrawals = null,
        ulong? slotNumber = null) => new()
        {
            Timestamp = parentHeader.Timestamp + 12,
            PrevRandao = Keccak.Compute("randao"),
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = withdrawals ?? [],
            ParentBeaconBlockRoot = Keccak.Compute("parentBeaconBlockRoot"),
            SlotNumber = slotNumber
        };

    private static Transaction[] BuildSignedTransactions(int count)
    {
        Transaction[] transactions = new Transaction[count];

        for (int i = 0; i < count; i++)
        {
            transactions[i] = Core.Test.Builders.Build.A.Transaction
                .WithNonce((UInt256)i)
                .WithTimestamp((UInt256)(1_000 + i))
                .WithTo(Core.Test.Builders.TestItem.AddressC)
                .WithValue(i + 1)
                .WithGasLimit(21_000)
                .WithType(TxType.EIP1559)
                .WithChainId(1)
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(Core.Test.Builders.TestItem.PrivateKeyA)
                .TestObject;
        }

        return transactions;
    }

    private static byte[][] EncodeTransactions(Transaction[] transactions, out string[] txHex)
    {
        byte[][] txRlps = new byte[transactions.Length][];
        txHex = new string[transactions.Length];

        for (int i = 0; i < transactions.Length; i++)
        {
            byte[] encoded = TxDecoder.Instance.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
            txRlps[i] = encoded;
            txHex[i] = encoded.ToHexString(true);
        }

        return txRlps;
    }
}
