// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data, Is.AssignableTo<GetPayloadV5Result>());
        GetPayloadV5Result payloadResult = (GetPayloadV5Result)result.Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payloadResult.ExecutionPayload.BlobGasUsed, Is.EqualTo(0));
            Assert.That(payloadResult.ExecutionPayload.ExcessBlobGas, Is.EqualTo(BlobGasCalculator.CalculateExcessBlobGas(parentHeader, Osaka.Instance)));
            Assert.That(suggestedWithdrawalsRoot, Is.EqualTo(new WithdrawalTrie(payloadAttributes.Withdrawals!).RootHash));
        }
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

        Assert.That(response, Is.TypeOf<ResultWrapper<object>>());
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
        Assert.That(root.TryGetProperty("error", out _), Is.False);

        JsonElement executionPayload = root.GetProperty("result").GetProperty("executionPayload");
        JsonElement transactionsJson = executionPayload.GetProperty("transactions");

        Assert.That(transactionsJson.GetArrayLength(), Is.EqualTo(txHex.Length));
        for (int i = 0; i < txHex.Length; i++)
        {
            Assert.That(transactionsJson[i].GetString(), Is.EqualTo(txHex[i]));
        }

        if (expectsBlockAccessList)
        {
            Assert.That(executionPayload.TryGetProperty("blockAccessList", out JsonElement blockAccessList), Is.True);
            Assert.That(blockAccessList.GetString(), Is.Not.Null.And.Not.Empty);
        }
        else
        {
            Assert.That(executionPayload.TryGetProperty("blockAccessList", out _), Is.False);
        }

        if (expectsSlotNumber)
        {
            Assert.That(executionPayload.TryGetProperty("slotNumber", out JsonElement slotNumberJson), Is.True);
            Assert.That(slotNumberJson.GetString(), Is.EqualTo(slotNumber!.Value.ToHexString(skipLeadingZeros: true)));
        }
        else
        {
            Assert.That(executionPayload.TryGetProperty("slotNumber", out _), Is.False);
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
        txSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns(new[] { mempoolTx });

        (TestingRpcModule module, Hash256 parentHash, BlockHeader parentHeader) = CreateBuildTestingModule(txSource: txSource);

        byte[][]? txRlps = useNull ? null : [];
        ResultWrapper<object> result = await module.testing_buildBlockV1(
            parentHash, CreateDefaultPayloadAttributes(parentHeader), txRlps, []);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(((GetPayloadV5Result)result.Data!).ExecutionPayload.Transactions.Length, Is.EqualTo(expectedTxCount));

        if (useNull)
            txSource.Received(1).GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), true);
        else
            txSource.DidNotReceive().GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>());
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Result.Error, Does.Contain("expected 2 transactions but only 1 were included"));
        }
    }

    [Test]
    public async Task Returns_error_for_unknown_parent()
    {
        (TestingRpcModule module, _, BlockHeader parentHeader) = CreateBuildTestingModule();

        Hash256 unknownHash = Keccak.Compute("unknown");
        ResultWrapper<object> result = await module.testing_buildBlockV1(
            unknownHash, CreateDefaultPayloadAttributes(parentHeader), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Result.Error, Does.Contain("unknown parent block"));
        }
    }

    [Test]
    public async Task Testing_commitBlockV1_commits_block_to_chain_head()
    {
        (TestingRpcModule module, RecordingCommitBlockTree blockTree, BlockHeader chainHeadHeader) =
            CreateCommitTestingModule(suggestResult: AddBlockResult.Added);

        ResultWrapper<Hash256> result = await module.testing_commitBlockV1(
            CreateDefaultPayloadAttributes(chainHeadHeader),
            [],
            []);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));

        Block suggested = blockTree.SuggestedBlock!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(suggested.Header.Number, Is.EqualTo(chainHeadHeader.Number + 1));
            Assert.That(suggested.Hash, Is.Not.Null);
            Assert.That(result.Data, Is.EqualTo(suggested.Hash!));
        }
    }

    [Test]
    public async Task Testing_commitBlockV1_skips_reprocessing_by_setting_main_chain_directly()
    {
        (TestingRpcModule module, RecordingCommitBlockTree blockTree, BlockHeader chainHeadHeader) =
            CreateCommitTestingModule(suggestResult: AddBlockResult.Added);

        ResultWrapper<Hash256> result = await module.testing_commitBlockV1(
            CreateDefaultPayloadAttributes(chainHeadHeader), [], []);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));

        Assert.That(blockTree.SuggestOptions, Is.EqualTo(BlockTreeSuggestOptions.ForceDontSetAsMain),
            "ShouldProcess would force the main BlockchainProcessor to re-execute every tx; " +
            "ForceDontSetAsMain leaves the main-chain write to TryUpdateMainChain (single writer).");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.TryUpdatePreloadedBlocks?.Length, Is.EqualTo(1), "the already-executed block is handed over as the preloaded cache, not re-read");
            Assert.That(blockTree.TryUpdateWereProcessed, Is.True, "the producer already executed the block; the main chain must reflect that");
            Assert.That(blockTree.TryUpdateForceUpdateHeadBlock, Is.True,
                "post-merge chains have TotalDifficulty=0; without forceUpdateHeadBlock MoveToMain skips UpdateHeadBlock and the next commit reads a stale head.");
        }
    }

    [Test]
    public async Task Testing_commitBlockV1_passes_correct_flags_to_producer()
    {
        // Options must mirror BlockProducerBase.GetProcessingOptions for BuildBlocksOnMainState;
        // state persistence itself is covered end-to-end by TestingRpcModuleBlockchainTests.
        ProcessingOptions? observedOptions = null;
        (TestingRpcModule module, _, BlockHeader chainHeadHeader) =
            CreateCommitTestingModule(suggestResult: AddBlockResult.Added,
                onProcess: (block, opts) => observedOptions = opts);

        await module.testing_commitBlockV1(CreateDefaultPayloadAttributes(chainHeadHeader), [], []);

        Assert.That(observedOptions, Is.Not.Null);
        ProcessingOptions opts = observedOptions!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(opts.ContainsFlag(ProcessingOptions.StoreReceipts), Is.True);
            Assert.That(opts.ContainsFlag(ProcessingOptions.NoValidation), Is.True);
            Assert.That(opts.ContainsFlag(ProcessingOptions.ForceProcessing), Is.True);
            Assert.That(opts.ContainsFlag(ProcessingOptions.DoNotUpdateHead), Is.True);
            Assert.That(opts.ContainsFlag(ProcessingOptions.ReadOnlyChain), Is.False);
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
        }
    }

    [Test]
    public async Task Testing_commitBlockV1_fails_when_block_commit_fails()
    {
        (TestingRpcModule module, _, BlockHeader chainHeadHeader) =
            CreateCommitTestingModule(suggestResult: AddBlockResult.InvalidBlock);

        ResultWrapper<Hash256> result = await module.testing_commitBlockV1(
            CreateDefaultPayloadAttributes(chainHeadHeader),
            [],
            null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
        }
    }

    [Test]
    public async Task Testing_commitBlockV1_fails_on_invalid_transaction_rlp()
    {
        (TestingRpcModule module, _, BlockHeader chainHeadHeader) = CreateCommitTestingModule();

        ResultWrapper<Hash256> result = await module.testing_commitBlockV1(
            CreateDefaultPayloadAttributes(chainHeadHeader),
            new[] { new byte[] { 0xff, 0xff, 0xff } },
            null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Result.Error, Does.Contain("invalid transaction RLP"));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput));
        }
    }

    private (TestingRpcModule module, IBlockTree blockTree, IBlockFinder blockFinder, BlockHeader parentHeader) CreateModuleWithMocks(
        IReleaseSpec? spec = null,
        ulong? slotNumber = null,
        Action<Block>? onProcess = null,
        Func<Block, Block?>? processOverride = null,
        ITxSource? txSource = null,
        Action<Block, ProcessingOptions>? onProcessWithOptions = null,
        IBlockTree? blockTree = null)
    {
        BlockHeader parentHeader = CreateDefaultParentHeader(slotNumber);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec ?? Osaka.Instance);

        IGasLimitCalculator gasLimitCalculator = Substitute.For<IGasLimitCalculator>();
        gasLimitCalculator.GetGasLimit(Arg.Any<BlockHeader>()).Returns(parentHeader.GasLimit);
        gasLimitCalculator.GetGasLimit(Arg.Any<BlockHeader>(), Arg.Any<ulong?>()).Returns(parentHeader.GasLimit);

        IBlockchainProcessor blockchainProcessor = CreateBlockProcessor(processOverride, onProcess, onProcessWithOptions);

        IBlockProducerEnv blockProducerEnv = Substitute.For<IBlockProducerEnv>();
        blockProducerEnv.ChainProcessor.Returns(blockchainProcessor);
        if (txSource is not null)
            blockProducerEnv.TxSource.Returns(txSource);

        IBlockProducerEnvFactory blockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>();
        blockProducerEnvFactory.CreatePersistent().Returns(blockProducerEnv);
        blockProducerEnvFactory.CreateTransient().Returns(new ScopedBlockProducerEnv(blockProducerEnv, Substitute.For<IAsyncDisposable>()));

        IMainStateBlockProducerEnvFactory mainStateBlockProducerEnvFactory = Substitute.For<IMainStateBlockProducerEnvFactory>();
        mainStateBlockProducerEnvFactory.CreatePersistent().Returns(blockProducerEnv);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IBlockTree tree = blockTree ?? Substitute.For<IBlockTree>();

        TestingRpcModule module = new(blockProducerEnvFactory, mainStateBlockProducerEnvFactory, gasLimitCalculator, specProvider, blockFinder, tree, Substitute.For<IProcessExitSource>(), LimboLogs.Instance);
        _disposables.Add(module);
        return (module, tree, blockFinder, parentHeader);
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

    private (TestingRpcModule module, RecordingCommitBlockTree blockTree, BlockHeader chainHeadHeader) CreateCommitTestingModule(
        AddBlockResult suggestResult = AddBlockResult.Added,
        bool nullChainHead = false,
        Action<Block, ProcessingOptions>? onProcess = null)
    {
        RecordingCommitBlockTree recordingTree = new();
        (TestingRpcModule module, _, _, BlockHeader chainHeadHeader) =
            CreateModuleWithMocks(onProcessWithOptions: onProcess, blockTree: recordingTree);

        if (!nullChainHead)
            recordingTree.Head = new Block(chainHeadHeader, [], [], []);
        recordingTree.SuggestResult = suggestResult;

        return (module, recordingTree, chainHeadHeader);
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
        Action<Block>? onProcess = null,
        Action<Block, ProcessingOptions>? onProcessWithOptions = null)
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

        if (onProcessWithOptions is not null)
        {
            processor
                .When(x => x.Process(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<CancellationToken>()))
                .Do(callInfo => onProcessWithOptions(callInfo.Arg<Block>(), callInfo.Arg<ProcessingOptions>()));
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

        for (uint i = 0; i < count; i++)
        {
            transactions[i] = Core.Test.Builders.Build.A.Transaction
                .WithNonce(i)
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
