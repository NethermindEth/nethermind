// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
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
    private readonly List<IDisposable> _disposables = [];

    [TearDown]
    public void TearDown()
    {
        foreach (IDisposable disposable in _disposables) disposable.Dispose();
        _disposables.Clear();
    }

    [Test]
    public async Task Sets_excess_blob_gas_and_withdrawals_root()
    {
        Hash256? suggestedWithdrawalsRoot = null;
        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateBuildTestingModule(
            onProcess: block => suggestedWithdrawalsRoot = block.Header.WithdrawalsRoot);

        PayloadAttributes payloadAttributes = CreateDefaultPayloadAttributes(parentHeader,
            withdrawals: [new Withdrawal { Index = 0, ValidatorIndex = 0, Address = Address.Zero, AmountInGwei = 1 }]);

        ResultWrapper<object> result = await module.testing_buildBlockV1(parentHash, payloadAttributes, [], []);

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
        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateBuildTestingModule();

        JsonRpcResponse response = await RpcTest.TestRequest<ITestingRpcModule>(
            module,
            nameof(ITestingRpcModule.testing_buildBlockV1),
            parentHash,
            CreateDefaultPayloadAttributes(parentHeader),
            (byte[][])[]);

        response.Should().BeOfType<JsonRpcSuccessResponse>();
    }

    [TestCaseSource(nameof(BuildBlockV1ForkCases))]
    public async Task Returns_fork_specific_payload(
        IReleaseSpec spec,
        bool expectsBlockAccessList,
        bool expectsSlotNumber)
    {
        ulong? parentSlot = expectsSlotNumber ? 1UL : null;
        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateBuildTestingModule(
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
            (byte[])[]);

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

        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateBuildTestingModule(txSource: txSource);

        byte[][]? txRlps = useNull ? null : [];
        ResultWrapper<object> result = await module.testing_buildBlockV1(
            parentHash, CreateDefaultPayloadAttributes(parentHeader), txRlps, []);

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
        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateBuildTestingModule(
            processOverride: block =>
            {
                Block processedBlock = new(block.Header, [block.Transactions[0]], [], block.Withdrawals);
                processedBlock.Header.StateRoot ??= Keccak.EmptyTreeHash;
                processedBlock.Header.ReceiptsRoot ??= Keccak.EmptyTreeHash;
                processedBlock.Header.Bloom ??= Bloom.Empty;
                processedBlock.Header.GasUsed = 21_000;
                processedBlock.Header.Hash ??= Keccak.Compute("produced");
                return processedBlock;
            });

        byte[][] txRlps = EncodeTransactions(BuildSignedTransactions(2), out _);

        ResultWrapper<object> result = await module.testing_buildBlockV1(
            parentHash, CreateDefaultPayloadAttributes(parentHeader), txRlps, []);

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("expected 2 transactions but only 1 were included");
    }

    [Test]
    public async Task Returns_error_for_unknown_parent()
    {
        (TestingRpcModule module, _, BlockHeader parentHeader) = CreateBuildTestingModule();

        Hash256 unknownHash = Keccak.Compute("unknown");
        ResultWrapper<object> result = await module.testing_buildBlockV1(
            unknownHash, CreateDefaultPayloadAttributes(parentHeader), null);

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("unknown parent block");
    }

    [Test]
    public async Task Testing_commitBlockV1_commits_block_to_chain_head()
    {
        (TestingRpcModule module, IBlockTree blockTree, BlockHeader chainHeadHeader) =
            CreateCommitTestingModule(suggestResult: AddBlockResult.Added);

        ResultWrapper<Hash256> result = await module.testing_commitBlockV1(
            CreateDefaultPayloadAttributes(chainHeadHeader),
            [],
            []);

        result.Result.ResultType.Should().Be(ResultType.Success);

        Block suggested = (Block)blockTree.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IBlockTree.SuggestBlockAsync))
            .GetArguments()[0]!;
        suggested.Header.Number.Should().Be(chainHeadHeader.Number + 1);
        suggested.Hash.Should().NotBeNull();
        result.Data.Should().Be(suggested.Hash!);
    }

    [Test]
    public async Task Testing_commitBlockV1_fails_when_chain_head_not_found()
    {
        (TestingRpcModule module, _, BlockHeader chainHeadHeader) =
            CreateCommitTestingModule(nullChainHead: true);

        ResultWrapper<Hash256> result = await module.testing_commitBlockV1(
            CreateDefaultPayloadAttributes(chainHeadHeader),
            [],
            null);

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public async Task Testing_commitBlockV1_fails_when_block_commit_fails()
    {
        (TestingRpcModule module, _, BlockHeader chainHeadHeader) =
            CreateCommitTestingModule(suggestResult: AddBlockResult.InvalidBlock, fireNewHeadEvent: false);

        ResultWrapper<Hash256> result = await module.testing_commitBlockV1(
            CreateDefaultPayloadAttributes(chainHeadHeader),
            [],
            null);

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public async Task Testing_commitBlockV1_fails_on_invalid_transaction_rlp()
    {
        (TestingRpcModule module, _, BlockHeader chainHeadHeader) = CreateCommitTestingModule();

        ResultWrapper<Hash256> result = await module.testing_commitBlockV1(
            CreateDefaultPayloadAttributes(chainHeadHeader),
            new[] { new byte[] { 0xff, 0xff, 0xff } },
            null);

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("invalid transaction RLP");
        result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
    }

    private (TestingRpcModule module, IBlockTree blockTree, IBlockFinder blockFinder, BlockHeader parentHeader) CreateModuleWithMocks(
        IReleaseSpec? spec = null,
        ulong? slotNumber = null,
        Action<Block>? onProcess = null,
        Func<Block, Block?>? processOverride = null,
        ITxSource? txSource = null)
    {
        BlockHeader parentHeader = CreateDefaultParentHeader(slotNumber);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec ?? Osaka.Instance);

        IGasLimitCalculator gasLimitCalculator = Substitute.For<IGasLimitCalculator>();
        gasLimitCalculator.GetGasLimit(Arg.Any<BlockHeader>()).Returns(parentHeader.GasLimit);

        IBlockchainProcessor blockchainProcessor = CreateBlockProcessor(processOverride, onProcess);

        IBlockProducerEnv blockProducerEnv = Substitute.For<IBlockProducerEnv>();
        blockProducerEnv.ChainProcessor.Returns(blockchainProcessor);
        if (txSource is not null)
            blockProducerEnv.TxSource.Returns(txSource);

        IBlockProducerEnvFactory blockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>();
        blockProducerEnvFactory.CreateTransient().Returns(new ScopedBlockProducerEnv(blockProducerEnv, Substitute.For<IAsyncDisposable>()));

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();

        TestingRpcModule module = new(blockProducerEnvFactory, gasLimitCalculator, specProvider, blockFinder, blockTree, Substitute.For<IProcessExitSource>(), LimboLogs.Instance);
        _disposables.Add(module);
        return (module, blockTree, blockFinder, parentHeader);
    }

    private (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) CreateBuildTestingModule(
        IReleaseSpec? spec = null,
        ulong? slotNumber = null,
        Action<Block>? onProcess = null,
        Func<Block, Block?>? processOverride = null,
        ITxSource? txSource = null)
    {
        (TestingRpcModule module, _, IBlockFinder blockFinder, BlockHeader parentHeader) =
            CreateModuleWithMocks(spec, slotNumber, onProcess, processOverride, txSource);

        Hash256 parentHash = parentHeader.Hash!;
        Block parentBlock = new(parentHeader, [], [], []);
        blockFinder.FindBlock(parentHash).Returns(parentBlock);

        return (module, parentHash, parentHeader);
    }

    private (TestingRpcModule module, IBlockTree blockTree, BlockHeader chainHeadHeader) CreateCommitTestingModule(
        AddBlockResult suggestResult = AddBlockResult.Added,
        bool fireNewHeadEvent = true,
        bool nullChainHead = false)
    {
        (TestingRpcModule module, IBlockTree blockTree, _, BlockHeader chainHeadHeader) = CreateModuleWithMocks();
        Block chainHeadBlock = new(chainHeadHeader, [], [], []);

        blockTree.Head.Returns(nullChainHead ? null : chainHeadBlock);

        // Raise NewHeadBlock inside Returns() so the event fires before SuggestBlockAsync
        // returns — matches the production subscribe-then-suggest ordering the endpoint relies on.
        if (fireNewHeadEvent && suggestResult == AddBlockResult.Added)
        {
            blockTree.SuggestBlockAsync(Arg.Any<Block>(), Arg.Any<BlockTreeSuggestOptions>())
                .Returns(callInfo =>
                {
                    Block block = callInfo.Arg<Block>();
                    blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(block));
                    return suggestResult;
                });
        }
        else
        {
            blockTree.SuggestBlockAsync(Arg.Any<Block>(), Arg.Any<BlockTreeSuggestOptions>())
                .Returns(suggestResult);
        }

        return (module, blockTree, chainHeadHeader);
    }

    private static BlockHeader CreateDefaultParentHeader(ulong? slotNumber = null) =>
        new(
            Keccak.Compute("grandparent"),
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.Zero,
            1,
            30_000_000,
            1,
            [])
        {
            Hash = Keccak.Compute("parent"),
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
