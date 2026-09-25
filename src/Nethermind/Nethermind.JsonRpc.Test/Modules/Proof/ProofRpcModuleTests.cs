// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using NUnit.Framework;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Headers;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using Nethermind.State.Proofs;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Nethermind.JsonRpc.Test.Modules.Proof;

[Parallelizable(ParallelScope.None)]
public class ProofRpcModuleTests
{
    private IProofRpcModule _proofRpcModule = null!;
    private IBlockTree _blockTree = null!;
    private IDbProvider _dbProvider = null!;
    private TestSpecProvider _specProvider = null!;
    private WorldStateManager _worldStateManager = null!;
    private IHeaderFinder _headerFinder = null!;
    private TestStateHeaderProvider _stateHeaderProvider = null!;
    private IReceiptStorage _receiptStorage = null!;
    private IContainer _container;

    private const string NullResultResponse = """{"jsonrpc":"2.0","result":null,"id":67}""";

    /// <summary>Position in block 1 the stale-index arrangements request; not 0, so serving the block's first
    /// transaction instead of the requested one is a visible failure.</summary>
    private const int StaleReceiptIndexTxIndex = 1;

    /// <summary>Logs the stale-index arrangements put on the transaction preceding the requested one; the only logs
    /// a prefix taken in block transaction order may count.</summary>
    private const int StaleReceiptIndexLogsBefore = 3;

    /// <summary>Logs the stale-index arrangements put on the requested transaction itself; more than one, so the
    /// offset the served receipt applies per log is pinned past its first entry.</summary>
    private const int StaleReceiptIndexLogsOnRequested = 2;

    [SetUp]
    public async Task Setup()
    {
        _dbProvider = await TestMemDbProvider.InitAsync();
        // The proof tests target block 1, whose parent is the genesis header built below.
        _stateHeaderProvider = new();
        _worldStateManager = TestWorldStateFactory.CreateWorldStateManagerForTest(_dbProvider, _stateHeaderProvider, LimboLogs.Instance);

        Hash256 stateRoot;
        IWorldState worldState = new WorldState(_worldStateManager.GlobalWorldState, LimboLogs.Instance);
        using (IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 100000);
            worldState.Commit(London.Instance);
            worldState.CommitTree(0);
            stateRoot = worldState.StateRoot;
        }

        InMemoryReceiptStorage receiptStorage = new();
        _receiptStorage = receiptStorage;
        _specProvider = new TestSpecProvider(London.Instance);
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(new Block(Build.A.BlockHeader.WithStateRoot(stateRoot).TestObject, new BlockBody()), _specProvider)
            .WithTransactions(receiptStorage)
            .OfChainLength(10);
        _blockTree = blockTreeBuilder.TestObject;
        _stateHeaderProvider.Parent = _blockTree.FindHeader(0, BlockTreeLookupOptions.None);
        _headerFinder = blockTreeBuilder.HeaderStore;

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddSingleton<ISpecProvider>(_specProvider)
            .AddSingleton<IBlockTree>(_blockTree)
            .AddSingleton<IDbProvider>(_dbProvider)
            .AddSingleton<IHeaderFinder>(_headerFinder)
            .AddSingleton<IReceiptStorage>(_receiptStorage)
            .AddSingleton<IWorldStateManager>(_worldStateManager)
            .Build();
        _proofRpcModule = _container.Resolve<IRpcModuleFactory<IProofRpcModule>>().Create();
    }

    [TearDown]
    public void TearDown() => _container.Dispose();

    [Test]
    public async Task Can_get_transaction([Values] bool withHeader, [Values(0, 1)] int txIndex)
    {
        Hash256 txHash = _blockTree.FindBlock(1)!.Transactions[txIndex].Hash!;
        TransactionForRpcWithProof txWithProof = _proofRpcModule.proof_getTransactionByHash(txHash, withHeader).Data!;
        Assert.That(txWithProof.Transaction.Hash, Is.EqualTo(txHash));
        Assert.That(txWithProof.TxProof.Length, Is.EqualTo(2));
        if (withHeader)
        {
            Assert.That(txWithProof.BlockHeader, Is.Not.Null);
        }
        else
        {
            Assert.That(txWithProof.BlockHeader, Is.Null);
        }

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", txHash, withHeader);
        Assert.That(response.Contains("\"result\""), Is.True);
    }

    [Test]
    public async Task When_transaction_is_unknown_null_result_is_returned([Values] ProofMethod method, [Values] bool withHeader)
    {
        Hash256 txHash = TestItem.KeccakH;
        Assert.That(Invoke(method, txHash, withHeader).Data, Is.Null);

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, MethodName(method), txHash, withHeader);
        Assert.That(response, Is.EqualTo(NullResultResponse));
    }

    [Test]
    public async Task When_transaction_not_servable_transaction_by_hash_returns_null_result(
        [Values(NotServableScenario.HeaderNotFound, NotServableScenario.TransactionAbsentFromResolvedBlock)] NotServableScenario scenario)
    {
        Hash256 txHash = ArrangeNotServable(scenario);

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", txHash, false);
        Assert.That(response, Is.EqualTo(NullResultResponse));
    }

    /// <remarks>
    /// A transactionsRoot proof needs only the transaction's position in the resolved block, so receipts are not
    /// looked up. A deliberate divergence from <c>eth_getTransactionByHash</c>, which
    /// returns null when the receipt at the transaction's index is absent or carries a different hash.
    /// </remarks>
    [Test]
    public void When_only_the_receipt_is_missing_transaction_by_hash_still_serves_the_transaction(
        [Values(NotServableScenario.ReceiptsPruned, NotServableScenario.ReceiptsUnreproducible, NotServableScenario.MismatchedReceiptSet)] NotServableScenario scenario)
    {
        Block block = _blockTree.FindBlock(1)!;
        Hash256 txHash = ArrangeNotServable(scenario);

        TransactionForRpcWithProof? txWithProof = _proofRpcModule.proof_getTransactionByHash(txHash, false).Data;

        Assert.That(txWithProof, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(txWithProof.Transaction.Hash, Is.EqualTo(txHash));
            Assert.That(txWithProof.Transaction.TransactionIndex, Is.Zero);
            Assert.That(txWithProof.TxProof, Is.EqualTo(TxTrie.CalculateProof(block.Transactions, 0)));
        }
    }

    [Test]
    public async Task When_transaction_not_servable_transaction_receipt_returns_null_result(
        [Values(NotServableScenario.HeaderNotFound, NotServableScenario.ReceiptsPruned, NotServableScenario.MismatchedReceiptSet, NotServableScenario.TransactionAbsentFromResolvedBlock)] NotServableScenario scenario)
    {
        Hash256 txHash = ArrangeNotServable(scenario);

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, false);
        Assert.That(response, Is.EqualTo(NullResultResponse));
    }

    [Test]
    public async Task When_receipts_are_unreproducible_transaction_receipt_fails_with_pruned_history()
    {
        Hash256 txHash = ArrangeNotServable(NotServableScenario.ReceiptsUnreproducible);

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, false);
        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.PrunedHistoryUnavailable}"));
    }

    /// <remarks>
    /// Unlike <see cref="NotServableScenario"/>, a pruned block is a genuine <c>SearchForBlock</c> error, not an
    /// unknown-block miss — <c>TryResolveTransaction</c> must preserve it rather than fold it into a null result.
    /// </remarks>
    [Test]
    public void When_resolved_block_is_pruned_returns_the_preserved_error([Values] ProofMethod method)
    {
        Hash256 txHash = ArrangePrunedBlock();

        IResultWrapper result = Invoke(method, txHash, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.PrunedHistoryUnavailable));
        }
    }

    [Test]
    public void When_receipt_index_is_stale_transaction_by_hash_serves_the_requested_transaction([Values] StaleReceiptIndexScenario scenario)
    {
        Block block = _blockTree.FindBlock(1)!;
        Hash256 txHash = block.Transactions[StaleReceiptIndexTxIndex].Hash!;
        ArrangeStaleReceiptIndex(scenario);

        TransactionForRpcWithProof txWithProof = _proofRpcModule.proof_getTransactionByHash(txHash, false).Data!;

        Assert.That(txWithProof, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(txWithProof.Transaction.Hash, Is.EqualTo(txHash));
            Assert.That(txWithProof.Transaction.TransactionIndex, Is.EqualTo(StaleReceiptIndexTxIndex));
            Assert.That(txWithProof.TxProof, Is.EqualTo(TxTrie.CalculateProof(block.Transactions, StaleReceiptIndexTxIndex)));
        }
    }

    [Test]
    public void When_receipt_index_is_stale_transaction_receipt_proves_the_requested_transaction([Values] StaleReceiptIndexScenario scenario)
    {
        Block block = _blockTree.FindBlock(1)!;
        Hash256 txHash = block.Transactions[StaleReceiptIndexTxIndex].Hash!;
        // Ground truth for the proof content: the receipt trie is rebuilt from a fresh block retrace
        // (see BuildReceiptProofs), never from the (possibly stale) IReceiptFinder, so this unsubstituted
        // call for the same tx pins the same proof the stale-index arrangement below must still produce.
        byte[][] expectedReceiptProof = _proofRpcModule.proof_getTransactionReceipt(txHash, false).Data!.ReceiptProof;

        ArrangeStaleReceiptIndex(scenario);

        ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, false).Data!;

        Assert.That(receiptWithProof, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiptWithProof.Receipt.TransactionIndex, Is.EqualTo(StaleReceiptIndexTxIndex));
            Assert.That(receiptWithProof.Receipt.BlockHash, Is.EqualTo(block.Hash));
            Assert.That(receiptWithProof.Receipt.BlockNumber, Is.EqualTo(block.Number));
            Assert.That(receiptWithProof.Receipt.Logs, Has.Length.EqualTo(StaleReceiptIndexLogsOnRequested));
            // The starting offset is identical under both scenarios, so the stale stored Index moves nothing; and
            // non-zero, where the retraced block emits no logs at all, so the count came from the stored set the
            // served logs themselves come from. Every log carries it, not just the first.
            for (int i = 0; i < receiptWithProof.Receipt.Logs!.Length; i++)
            {
                Assert.That(receiptWithProof.Receipt.Logs[i].LogIndex, Is.EqualTo(StaleReceiptIndexLogsBefore + i), $"log {i} index");
                Assert.That(receiptWithProof.Receipt.Logs[i].TransactionIndex, Is.EqualTo(StaleReceiptIndexTxIndex), $"log {i} transaction index");
                Assert.That(receiptWithProof.Receipt.Logs[i].BlockHash, Is.EqualTo(block.Hash), $"log {i} block hash");
                Assert.That(receiptWithProof.Receipt.Logs[i].BlockNumber, Is.EqualTo(block.Number), $"log {i} block number");
            }
            Assert.That(receiptWithProof.TxProof, Is.EqualTo(TxTrie.CalculateProof(block.Transactions, StaleReceiptIndexTxIndex)));
            Assert.That(receiptWithProof.ReceiptProof, Is.EqualTo(expectedReceiptProof));
        }
    }

    /// <remarks>
    /// The receipt guard runs before the tracer environment is built, so a transaction the method cannot serve
    /// never opens an overridable environment it has no use for.
    /// </remarks>
    [Test]
    public void When_transaction_not_servable_transaction_receipt_does_not_build_tracer_env()
    {
        IOverridableEnv<ITracer> tracerEnv = Substitute.For<IOverridableEnv<ITracer>>();
        Hash256 txHash = ArrangeNotServable(NotServableScenario.MismatchedReceiptSet, tracerEnv);

        ResultWrapper<ReceiptWithProof?> result = _proofRpcModule.proof_getTransactionReceipt(txHash, false);

        tracerEnv.DidNotReceiveWithAnyArgs().TryBuildAndOverrideAtTarget(null!, null, null, out _);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
    }

    /// <remarks>
    /// Pins the receipt-count backstop, which the real tracer does not reach once the parent check has passed, hence
    /// the substituted <see cref="ITracer"/>. <c>ReceiptTrie.CalculateReceiptProofs</c> does not throw on a short set:
    /// it walks toward the encoded index rather than indexing the array, so without the backstop the caller is served
    /// a Success whose <c>receiptProof</c> is an empty array.
    /// </remarks>
    [Test]
    public async Task When_the_retrace_yields_no_receipts_transaction_receipt_fails_instead_of_proving_an_empty_trie()
    {
        Block block = _blockTree.FindBlock(1)!;
        Hash256 txHash = block.Transactions[0].Hash!;

        ITracer tracer = Substitute.For<ITracer>();
        IDisposable scopeCloser = Substitute.For<IDisposable>();
        IOverridableEnv<ITracer> tracerEnv = Substitute.For<IOverridableEnv<ITracer>>();
        tracerEnv.TryBuildAndOverrideAtTarget(null!, null, null, out _).ReturnsForAnyArgs(call =>
        {
            call[3] = new Scope<ITracer>(tracer, scopeCloser);
            return true;
        });
        // Only the retrace is substituted: the block and the stored receipts it is asked to prove are both real.
        ArrangeReceiptFinder(block, txHash, _receiptStorage.Get(block), tracerEnv);

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, false);

        // The receipts tracer is left empty only if the substitute really stood in for the block retrace.
        tracer.ReceivedWithAnyArgs(1).Trace(null!, null!);
        scopeCloser.Received(1).Dispose();
        Assert.That(response, Is.EqualTo(
            """{"jsonrpc":"2.0","error":{"code":-32002,"message":"Unable to re-execute block 1 (0xda4b91...9f813d) for a receipt proof"},"id":67}"""));
    }

    /// <remarks>
    /// Runs the real tracer environment. Without the parent check, an unknown parent retraces over empty state and
    /// fails on the first transaction with "insufficient funds", and a pruned one with a missing trie node.
    /// </remarks>
    [Test]
    public void When_parent_state_is_unavailable_transaction_receipt_fails_with_resource_unavailable([Values] bool parentHeaderKnown)
    {
        Block block = _blockTree.FindBlock(1)!;
        Hash256 txHash = block.Transactions[0].Hash!;

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(txHash).Returns(block.Hash);
        receiptFinder.Get(Arg.Any<Block>()).Returns(_receiptStorage.Get(block));
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        // A known parent whose state root the node no longer holds, or no parent at all. The header must carry the
        // block's own parent hash, or the "known" case would resolve to nothing and repeat the unknown one.
        _stateHeaderProvider.Parent = parentHeaderKnown
            ? Build.A.BlockHeader.WithNumber(0).WithHash(block.ParentHash!).WithStateRoot(TestItem.KeccakH).TestObject
            : null;
        RebuildContainerWith(receiptFinder, blockFinder: blockFinder);

        ResultWrapper<ReceiptWithProof?> result = _proofRpcModule.proof_getTransactionReceipt(txHash, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
        }
    }

    /// <summary>
    /// A transaction <c>proof_getTransactionReceipt</c> cannot serve, either because its block cannot be resolved at
    /// all, or because a resolved block and receipt set cannot serve it.
    /// </summary>
    /// <remarks>
    /// Only the first and last also stop <c>proof_getTransactionByHash</c>: the receipt-set scenarios leave the
    /// transaction in the resolved block, which is all its proof needs.
    /// </remarks>
    public enum NotServableScenario
    {
        /// <summary>The receipt store's recorded block hash for this tx no longer resolves to any block the
        /// block finder holds — <c>SearchForBlock</c> fails with <see cref="BlockFinderExtensions.HeaderNotFound"/>.</summary>
        HeaderNotFound,

        /// <summary>The resolved block has no receipts at all, as on a node that does not regenerate them.</summary>
        ReceiptsPruned,

        /// <summary>The resolved block's receipts are neither stored nor reproducible, for which the wired
        /// <c>RegeneratingReceiptFinder</c> throws rather than returning an empty set.</summary>
        ReceiptsUnreproducible,

        /// <summary>The resolved block's receipts are for other transactions — what a compact transaction index
        /// leaves behind once a reorg re-resolves the stored block number to a different canonical block.</summary>
        MismatchedReceiptSet,

        /// <summary>The resolved block does not carry the requested transaction at all, so it has no position in it
        /// to prove, even though a receipt claiming that hash is present.</summary>
        TransactionAbsentFromResolvedBlock
    }

    /// <summary>
    /// A receipt whose stored <c>Index</c> disagrees with the requested transaction's position in the resolved block.
    /// </summary>
    /// <remarks>
    /// Reachable: <c>ReceiptsRecovery.TryRecover</c> leaves a stored <c>Index</c> intact unless the set matches the
    /// block's transaction count and some receipt is missing a recoverable field.
    /// </remarks>
    public enum StaleReceiptIndexScenario
    {
        /// <summary>The stored <c>Index</c> falls past the end of the block's transaction list.</summary>
        BeyondBlockTransactions,

        /// <summary>The stored <c>Index</c> falls inside the block's transaction list, but on a different transaction.</summary>
        PointsToDifferentTransaction
    }

    private Hash256 ArrangeNotServable(NotServableScenario scenario, IOverridableEnv<ITracer>? tracerEnv = null)
    {
        if (scenario is NotServableScenario.HeaderNotFound)
        {
            return ArrangeHeaderNotFound(tracerEnv);
        }

        Block block = _blockTree.FindBlock(1)!;
        if (scenario is NotServableScenario.ReceiptsUnreproducible)
        {
            Hash256 unreproducibleTxHash = block.Transactions[0].Hash!;
            IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
            receiptFinder.FindBlockHash(unreproducibleTxHash).Returns(block.Hash);
            receiptFinder.Get(Arg.Any<Block>()).Throws(new ResourceNotFoundException("receipts neither stored nor reproducible"));
            RebuildContainerWith(receiptFinder, tracerEnv);
            return unreproducibleTxHash;
        }

        Hash256 txHash = scenario is NotServableScenario.TransactionAbsentFromResolvedBlock
            ? TestItem.KeccakA
            : block.Transactions[0].Hash!;
        TxReceipt[] receipts = scenario switch
        {
            NotServableScenario.ReceiptsPruned => [],
            NotServableScenario.MismatchedReceiptSet => [new TxReceipt { TxHash = TestItem.KeccakH, Index = 0 }],
            NotServableScenario.TransactionAbsentFromResolvedBlock => [new TxReceipt { TxHash = txHash, Index = 0 }],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        ArrangeReceiptFinder(block, txHash, receipts, tracerEnv);

        return txHash;
    }

    /// <remarks>
    /// Points the receipt store at a block hash the (real) block tree does not know, reproducing
    /// <c>SearchForBlock</c>'s <see cref="BlockFinderExtensions.HeaderNotFound"/> path.
    /// </remarks>
    private Hash256 ArrangeHeaderNotFound(IOverridableEnv<ITracer>? tracerEnv)
    {
        Hash256 txHash = TestItem.KeccakB;
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(txHash).Returns(TestItem.KeccakC);
        RebuildContainerWith(receiptFinder, tracerEnv);

        return txHash;
    }

    /// <remarks>
    /// Reproduces <c>SearchForBlock</c>'s <see cref="ErrorCodes.PrunedHistoryUnavailable"/> path: the resolved
    /// block's header is known but its body has been pruned below the served floor. The real test chain has no
    /// pruned blocks, so the block finder is substituted directly rather than the block tree.
    /// </remarks>
    private Hash256 ArrangePrunedBlock()
    {
        Hash256 txHash = TestItem.KeccakD;
        Hash256 blockHash = TestItem.KeccakE;
        BlockHeader prunedHeader = Build.A.BlockHeader.WithNumber(1).TestObject;

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(txHash).Returns(blockHash);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(Build.A.Block.WithNumber(prunedHeader.Number + 1).TestObject);
        // The block-hash overload is a default interface member; stubbing it directly (rather than the
        // Hash256 overload it would otherwise delegate to) is what NSubstitute actually intercepts.
        blockFinder.FindHeader(Arg.Any<BlockParameter>()).Returns(prunedHeader);
        blockFinder.LowestServedBlock.Returns(prunedHeader.Number + 1);

        RebuildContainerWith(receiptFinder, blockFinder: blockFinder);

        return txHash;
    }

    /// <remarks>
    /// The substituted set is wider than the block and carries logs the block never emitted, so counting over it
    /// answers non-zero where the retraced receipts answer 0, which makes the served <c>logIndex</c> evidence of
    /// which of the two the receipt was served against. It separates the readings of the prefix as well: neither
    /// taking the array up to the requested position nor thresholding on the stored <c>Index</c> reaches the
    /// block's first receipt, and both admit receipts carrying logs of their own, so only matching by transaction
    /// hash answers <see cref="StaleReceiptIndexLogsBefore"/>.
    /// </remarks>
    private void ArrangeStaleReceiptIndex(StaleReceiptIndexScenario scenario)
    {
        Block block = _blockTree.FindBlock(1)!;
        Hash256 txHash = block.Transactions[StaleReceiptIndexTxIndex].Hash!;

        const int logsOnDroppedTransaction = 5;

        int staleIndex = scenario switch
        {
            StaleReceiptIndexScenario.BeyondBlockTransactions => block.Transactions.Length,
            StaleReceiptIndexScenario.PointsToDifferentTransaction => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        // A blob left by a block that began with a transaction this one no longer contains: that receipt leads,
        // so every receipt behind it sits one array position and one stored Index past where its transaction is,
        // except the requested receipt, whose stored Index the scenario sets.
        TxReceipt[] receipts =
        [
            ReceiptWithLogs(TestItem.KeccakF, 0, logsOnDroppedTransaction),
            ReceiptWithLogs(block.Transactions[0].Hash!, 1, StaleReceiptIndexLogsBefore),
            ReceiptWithLogs(txHash, staleIndex, StaleReceiptIndexLogsOnRequested)
        ];

        ArrangeReceiptFinder(block, txHash, receipts);
    }

    private static TxReceipt ReceiptWithLogs(Hash256 txHash, int index, int logCount)
    {
        LogEntry[] logs = new LogEntry[logCount];
        for (int i = 0; i < logCount; i++)
        {
            logs[i] = Build.A.LogEntry.TestObject;
        }

        // Block coordinates of another block, as a stale blob would carry.
        return new TxReceipt { TxHash = txHash, Index = index, Logs = logs, Bloom = new Bloom(logs), BlockHash = TestItem.KeccakH, BlockNumber = 5 };
    }

    public enum ProofMethod
    {
        TransactionByHash,
        TransactionReceipt
    }

    private IResultWrapper Invoke(ProofMethod method, Hash256 txHash, bool includeHeader) => method switch
    {
        ProofMethod.TransactionByHash => _proofRpcModule.proof_getTransactionByHash(txHash, includeHeader),
        ProofMethod.TransactionReceipt => _proofRpcModule.proof_getTransactionReceipt(txHash, includeHeader),
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static string MethodName(ProofMethod method) => method switch
    {
        ProofMethod.TransactionByHash => nameof(IProofRpcModule.proof_getTransactionByHash),
        ProofMethod.TransactionReceipt => nameof(IProofRpcModule.proof_getTransactionReceipt),
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private void ArrangeReceiptFinder(Block block, Hash256 txHash, TxReceipt[] receipts, IOverridableEnv<ITracer>? tracerEnv = null)
    {
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(txHash).Returns(block.Hash);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receipts);
        RebuildContainerWith(receiptFinder, tracerEnv);
    }

    /// <remarks>
    /// Swaps in a substituted <see cref="IReceiptFinder"/> while keeping the real block tree, so
    /// <c>FindBlockHash</c> and <c>Get(block)</c> can be driven independently — used to reproduce a
    /// resolved block whose receipt set doesn't contain the tx. A supplied <paramref name="tracerEnv"/>
    /// replaces the environment the receipt method traces the block in, and a supplied
    /// <paramref name="blockFinder"/> replaces the block tree's own <c>SearchForBlock</c> resolution —
    /// used to reproduce a search failure (e.g. pruned history) the real, fully-populated test chain
    /// cannot otherwise produce.
    /// </remarks>
    private void RebuildContainerWith(IReceiptFinder receiptFinder, IOverridableEnv<ITracer>? tracerEnv = null, IBlockFinder? blockFinder = null)
    {
        _container.Dispose();
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddSingleton<ISpecProvider>(_specProvider)
            .AddSingleton<IBlockTree>(_blockTree)
            .AddSingleton<IReceiptFinder>(receiptFinder)
            .AddSingleton<IDbProvider>(_dbProvider)
            .AddSingleton<IHeaderFinder>(_headerFinder)
            .AddSingleton<IReceiptStorage>(_receiptStorage)
            .AddSingleton<IWorldStateManager>(_worldStateManager);

        if (tracerEnv is not null)
        {
            builder.AddSingleton<IOverridableEnv<ITracer>>(tracerEnv);
        }

        if (blockFinder is not null)
        {
            builder.AddSingleton<IBlockFinder>(blockFinder);
        }

        _container = builder.Build();
        _proofRpcModule = _container.Resolve<IRpcModuleFactory<IProofRpcModule>>().Create();
    }

    [TestCase]
    public async Task On_incorrect_params_returns_correct_error_code()
    {
        Hash256 txHash = TestItem.KeccakH;

        // missing with header
        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "missing");

        // too many
        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, true, false);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "too many");

        // missing with header
        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", txHash);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "missing");

        // too many
        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", txHash, true, false);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "too many");

        // all wrong
        response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", txHash);
        Assert.That(response.Contains($"{ErrorCodes.InvalidParams}"), Is.True, "missing");
    }

    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x9d335cdd632432bc4181dabfc07b9a614f1fcf9f0d2c0c1340e35a403875fdb1\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x0000000000000000000000000000000000000000\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86530b862f860800182a41094000000000000000000000000000000000000000001818026a0e4830571029d291f22478cbb60a04115f783fb687f9c3a98bf9d4a008f909817a010f0f7a1c274747616522ea29771cb026bf153362227563e2657d25fa57816bd\"],\"receiptProof\":[\"0xf851a0970464c5f98c507970da7d4e5fc0600a9927d9563cf08ed113dc42772e3ff11080808080808080a07e2d58ec5d664555812c48866e548a5e2e5d51dd5b0540d0c59b6c08932e80818080808080808080\",\"0xf9010f30b9010bf9010801825218b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0a3e31eb259593976b3717142a5a9e90637f614d33e2ad13f01134ea00c24ca5aa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a009e11c477e0a0dfdfe036492b9bce7131991eb23bcf9575f9bff1e4016f90447a0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x9d335cdd632432bc4181dabfc07b9a614f1fcf9f0d2c0c1340e35a403875fdb1\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x0000000000000000000000000000000000000000\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86530b862f860800182a41094000000000000000000000000000000000000000001818026a0e4830571029d291f22478cbb60a04115f783fb687f9c3a98bf9d4a008f909817a010f0f7a1c274747616522ea29771cb026bf153362227563e2657d25fa57816bd\"],\"receiptProof\":[\"0xf851a0970464c5f98c507970da7d4e5fc0600a9927d9563cf08ed113dc42772e3ff11080808080808080a07e2d58ec5d664555812c48866e548a5e2e5d51dd5b0540d0c59b6c08932e80818080808080808080\",\"0xf9010f30b9010bf9010801825218b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
    public async Task Can_get_receipt(bool withHeader, string expectedResult)
    {
        Hash256 txHash = _blockTree.FindBlock(1)!.Transactions[0].Hash!;
        ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, withHeader).Data!;
        Assert.That(receiptWithProof.Receipt, Is.Not.Null);
        Assert.That(receiptWithProof.ReceiptProof.Length, Is.EqualTo(2));

        Assert.That(receiptWithProof.BlockHeader, withHeader ? Is.Not.Null : Is.Null);

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, withHeader);
        Assert.That(response, Is.EqualTo(expectedResult));
    }

    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xf4241\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xf4241\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86431b861f85f010182a410940000000000000000000000000000000000000000020126a0872929cb57ab6d88d0004a60f00df3dd9e0755860549aea25e559bce3d4a66dba01c06266ee2085ae815c258dd9dbb601bfc08c35c13b7cc9cd4ed88a16c3eb3f0\"],\"receiptProof\":[\"0xf851a0970464c5f98c507970da7d4e5fc0600a9927d9563cf08ed113dc42772e3ff11080808080808080a07e2d58ec5d664555812c48866e548a5e2e5d51dd5b0540d0c59b6c08932e80818080808080808080\",\"0xf9010f31b9010bf901080182a430b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0a3e31eb259593976b3717142a5a9e90637f614d33e2ad13f01134ea00c24ca5aa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a009e11c477e0a0dfdfe036492b9bce7131991eb23bcf9575f9bff1e4016f90447a0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xf4241\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x1\",\"transactionHash\":\"0x4901390ae91e8a4286f7ae9053440c48eb5c2bca11ca83439f0088a4af90ceb8\",\"blockHash\":\"0xda4b917515655b1aabcc9b01125df34a76c6ebb3e7e2f2b060d4daa70d9f813d\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xf4241\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a0eb9c9ef295ba68ff22c85763176dabc05773d58ef77ce34e4a23bf9516c706bc80808080808080a0850e08970f6beee9bd3687c74e591429cf6f65d5faf9db298ddc627ac4a26a1b8080808080808080\",\"0xf86431b861f85f010182a410940000000000000000000000000000000000000000020126a0872929cb57ab6d88d0004a60f00df3dd9e0755860549aea25e559bce3d4a66dba01c06266ee2085ae815c258dd9dbb601bfc08c35c13b7cc9cd4ed88a16c3eb3f0\"],\"receiptProof\":[\"0xf851a0970464c5f98c507970da7d4e5fc0600a9927d9563cf08ed113dc42772e3ff11080808080808080a07e2d58ec5d664555812c48866e548a5e2e5d51dd5b0540d0c59b6c08932e80818080808080808080\",\"0xf9010f31b9010bf901080182a430b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
    public async Task Get_receipt_when_block_has_few_receipts(bool withHeader, string expectedResult)
    {
        IReceiptFinder _receiptFinder = Substitute.For<IReceiptFinder>();
        LogEntry[] logEntries = new[] { Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject };

        TxReceipt receipt1 = new()
        {
            Bloom = new Bloom(logEntries),
            Index = 0,
            Recipient = TestItem.AddressA,
            Sender = TestItem.AddressB,
            BlockHash = _blockTree.FindBlock(1)!.Hash,
            BlockNumber = 1,
            ContractAddress = TestItem.AddressC,
            GasUsed = 1000,
            TxHash = _blockTree.FindBlock(1)!.Transactions[0].Hash,
            StatusCode = 0,
            GasUsedTotal = 2000,
            Logs = logEntries
        };

        TxReceipt receipt2 = new()
        {
            Bloom = new Bloom(logEntries),
            Index = 1,
            Recipient = TestItem.AddressC,
            Sender = TestItem.AddressD,
            BlockHash = _blockTree.FindBlock(1)!.Hash,
            BlockNumber = 1,
            ContractAddress = TestItem.AddressC,
            GasUsed = 1000,
            TxHash = _blockTree.FindBlock(1)!.Transactions[1].Hash,
            StatusCode = 0,
            GasUsedTotal = 2000,
            Logs = logEntries
        };
        _ = _blockTree.FindBlock(1)!;
        Hash256 txHash = _blockTree.FindBlock(1)!.Transactions[1].Hash!;
        TxReceipt[] receipts = { receipt1, receipt2 };
        _receiptFinder.Get(Arg.Any<Block>()).Returns(receipts);
        _receiptFinder.Get(Arg.Any<Hash256>()).Returns(receipts);
        _receiptFinder.FindBlockHash(Arg.Any<Hash256>()).Returns(_blockTree.FindBlock(1)!.Hash);

        RebuildContainerWith(_receiptFinder);
        ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, withHeader).Data!;

        if (withHeader)
        {
            Assert.That(receiptWithProof.BlockHeader, Is.Not.Null);
        }
        else
        {
            Assert.That(receiptWithProof.BlockHeader, Is.Null);
        }

        string response = await RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", txHash, withHeader);
        Assert.That(response, Is.EqualTo(expectedResult));
    }

}
