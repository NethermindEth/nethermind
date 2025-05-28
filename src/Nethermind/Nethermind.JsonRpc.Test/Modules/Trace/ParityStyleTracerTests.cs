// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.TxPool;
using NUnit.Framework;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Trie.Pruning;
using NSubstitute;
using Nethermind.Facade;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Test;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

[Parallelizable(ParallelScope.Self)]
public class ParityStyleTracerTests
{
#pragma warning disable NUnit1032 // An IDisposable field/property should be Disposed in a TearDown method
    private BlockchainProcessor? _processor;
#pragma warning restore NUnit1032 // An IDisposable field/property should be Disposed in a TearDown method
    private BlockTree? _blockTree;
    private Tracer? _tracer;
    private IPoSSwitcher? _poSSwitcher;
    private IStateReader _stateReader;
    private TraceRpcModule _traceRpcModule;
    private readonly IJsonRpcConfig _jsonRpcConfig = new JsonRpcConfig();

    [SetUp]
    public void Setup()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;

        _blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSpecProvider(specProvider)
            .TestObject;

        MemDb stateDb = new();
        MemDb codeDb = new();
        ITrieStore trieStore = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance).AsReadOnly();
        WorldState stateProvider = new(trieStore, codeDb, LimboLogs.Instance);
        _stateReader = new StateReader(trieStore, codeDb, LimboLogs.Instance);

        BlockhashProvider blockhashProvider = new(_blockTree, specProvider, stateProvider, LimboLogs.Instance);
        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(blockhashProvider, specProvider, LimboLogs.Instance);
        TransactionProcessor transactionProcessor = new(specProvider, stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);

        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        BlockProcessor blockProcessor = new BlockProcessor(
            specProvider,
            Always.Valid,
            new MergeRpcRewardCalculator(NoBlockRewards.Instance, _poSSwitcher),
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            new BlockhashStore(specProvider, stateProvider),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor));

        RecoverSignatures txRecovery = new(new EthereumEcdsa(TestBlockchainIds.ChainId), NullTxPool.Instance, specProvider, LimboLogs.Instance);
        _processor = new BlockchainProcessor(_blockTree, blockProcessor, txRecovery, _stateReader, LimboLogs.Instance, BlockchainProcessor.Options.NoReceipts);

        Block genesis = Build.A.Block.Genesis.TestObject;
        _blockTree.SuggestBlock(genesis);
        _processor.Process(genesis, ProcessingOptions.None, NullBlockTracer.Instance);

        IOverridableTxProcessorSource txProcessingSource = Substitute.For<IOverridableTxProcessorSource>();
        _tracer = new Tracer(new ReadOnlyTxProcessingScope(transactionProcessor, stateProvider, stateProvider.StateRoot), _processor, _processor);
        _traceRpcModule = new(NullReceiptStorage.Instance, _tracer, _blockTree, _jsonRpcConfig, _stateReader, txProcessingSource, Substitute.For<IBlockchainBridge>(), new BlocksConfig().SecondsPerSlot);
    }

    [TearDown]
    public async Task TearDownAsync() => await (_processor?.DisposeAsync() ?? default);

    [Test]
    public void Can_trace_raw_parity_style()
    {
        ResultWrapper<ParityTxTraceFromReplay> result = _traceRpcModule.trace_rawTransaction(Bytes.FromHexString("f889808609184e72a00082271094000000000000000000000000000000000000000080a47f74657374320000000000000000000000000000000000000000000000000000006000571ca08a8bbf888cfa37bbf0bb965423625641fc956967b81d12e23709cead01446075a01ce999b56a8a88504be365442ea61239198e23d1fce7d00fcfc5cd3b44b7215f"), new[] { "trace" });
        Assert.That(result.Data, Is.Not.Null);
    }

    [Test]
    public void Can_trace_raw_parity_style_berlin_tx()
    {
        ResultWrapper<ParityTxTraceFromReplay> result = _traceRpcModule.trace_rawTransaction(Bytes.FromHexString("01f85b821e8e8204d7847735940083030d408080853a60005500c080a0f43e70c79190701347517e283ef63753f6143a5225cbb500b14d98eadfb7616ba070893923d8a1fc97499f426524f9e82f8e0322dfac7c3d7e8a9eee515f0bcdc4"), new[] { "trace" });
        Assert.That(result.Data, Is.Not.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Should_return_correct_block_reward(bool isPostMerge)
    {
        Block block = Build.A.Block.WithParent(Build.A.Block.Genesis.TestObject).TestObject;
        _blockTree!.SuggestBlock(block).Should().Be(AddBlockResult.Added);
        _poSSwitcher!.IsPostMerge(Arg.Any<BlockHeader>()).Returns(isPostMerge);

        ParityTxTraceFromStore[] result = _traceRpcModule.trace_block(new BlockParameter(block.Number)).Data.ToArray();
        if (isPostMerge)
        {
            result.Length.Should().Be(1);
            result[0].Action.Author.Should().Be(block.Beneficiary!);
            result[0].Action.Value.Should().Be(0);
        }
        else
        {
            result.Length.Should().Be(0);
        }
    }
}
