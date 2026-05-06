// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Integration tests exercising EIP-8037 (bal-devnet-6) per-tx 2D block-gas accounting
/// against Nethermind's <c>BlockAccessListManager.IncrementalValidation</c>. Each test
/// mirrors a scenario from execution-specs PR 2703. The sequential executor regression
/// pins the same inclusion rule on the BAL-off/parallel-disabled path.
///
/// Tests pinned to <c>Assert.DoesNotThrow</c> verify spec acceptance; tests pinned to
/// <c>Assert.Throws&lt;InvalidBlockException&gt;</c> verify spec rejection. Failures
/// indicate Nethermind diverges from spec PR 2703 in that scenario.
///
/// </summary>
[Parallelizable(ParallelScope.All)]
public class Eip8037BlockGasIntegrationTests
{
    private const long Cpsb = 1174;
    private const long IntrinsicNewAccountState = 112 * Cpsb; // 131_488

    private static BlockAccessListManager CreateAmsterdamBalManager()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        return new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance));
    }

    private static (BlockAccessListManager, Block) BuildAmsterdamBlock(long blockGasLimit, params Transaction[] txs)
    {
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(blockGasLimit)
            .WithTransactions(txs)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Amsterdam.Instance));
        balManager.Setup(block);
        return (balManager, block);
    }

    private static TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[]
        ResultsForCount(int n)
    {
        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] arr =
            new TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[n];
        for (int i = 0; i < n; i++) arr[i] = new();
        return arr;
    }

    private static (long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)
        GasResult(Block block, int txIndex, long blockGasUsed, long blockStateGasUsed, InvalidBlockException? exception = null)
        => (blockGasUsed, blockStateGasUsed, EthereumGasPolicy.CalculateIntrinsicGas(block.Transactions[txIndex], Amsterdam.Instance, block.Header.GasLimit), exception);

    /// <summary>
    /// PR 2703 boundary[exact_fit]: post-tx cumulative state hits limit exactly.
    /// IncrementalValidation uses strict <c>&gt;</c>, so cumS == limit must be accepted.
    /// </summary>
    [Test]
    public void Spec_pr2703_boundary_state_exact_fit_accepts()
    {
        long blockGasLimit = 200_000;
        Transaction tx1 = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(50_000).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, tx1);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] results = ResultsForCount(1);
        // cumR = 50_000, cumS = 200_000 (exact fit). max = 200_000 == limit -> accept.
        results[0].SetResult(GasResult(block, 0, 50_000, 200_000));

        Assert.DoesNotThrow(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, Task.CompletedTask, CancellationToken.None));
        Assert.That(block.Header.GasUsed, Is.EqualTo(200_000));
    }

    /// <summary>
    /// PR 2703 boundary[exceeded]: post-tx cumulative state exceeds limit by 1 -> reject.
    /// </summary>
    [Test]
    public void Spec_pr2703_boundary_state_exceeded_by_one_rejects()
    {
        long blockGasLimit = 200_000;
        Transaction tx1 = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(50_000).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, tx1);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] results = ResultsForCount(1);
        // cumS = 200_001 -> max = 200_001 > 200_000 -> reject.
        results[0].SetResult(GasResult(block, 0, 50_000, 200_001));

        InvalidBlockException? ex = Assert.Throws<InvalidBlockException>(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, Task.CompletedTask, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("Block gas limit exceeded"));
    }

    /// <summary>
    /// PR 2703 <c>test_creation_tx_regular_check_subtracts_intrinsic_state</c>:
    /// in the spec scenario the creation tx is accepted at inclusion because the new
    /// formula subtracts <c>intrinsic.state</c>. With actual post-execution gas modest
    /// (the create succeeds well under cap), <c>IncrementalValidation</c> also accepts.
    /// This test verifies acceptance.
    /// </summary>
    [Test]
    public void Spec_pr2703_creation_tx_regular_check_actual_usage_modest_accepts()
    {
        long blockGasLimit = 16_777_216 + 53_000 + 1; // cap + intrinsic_regular + 1
        Transaction filler = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216).TestObject;
        Transaction createTx = Build.A.Transaction.WithHash(TestItem.KeccakB)
            .WithCode([])
            .WithGasLimit(53_000 + IntrinsicNewAccountState)
            .WithNonce(1).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, filler, createTx);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] results = ResultsForCount(2);
        // Filler used full cap; create tx used modest regular + intrinsic state.
        results[0].SetResult(GasResult(block, 0, 16_777_216, 0));
        results[1].SetResult(GasResult(block, 1, 53_000, IntrinsicNewAccountState));

        Assert.DoesNotThrow(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[2], null, Task.CompletedTask, CancellationToken.None));
    }

    /// <summary>
    /// PR 2703 <c>test_single_tx_state_check_exceeds_block_limit</c>: a single tx
    /// whose worst-case state contribution exceeds <c>block_gas_limit</c> must be
    /// rejected. Spec rejects at inclusion (pre-execution).
    ///
    /// Here we simulate Nethermind running the tx anyway and report modest actual
    /// post-execution gas (well under limit). Spec says reject; if Nethermind
    /// accepts because IncrementalValidation only sees post-execution numbers,
    /// this test FAILS - exposing the missing pre-tx inclusion check.
    /// </summary>
    [Test]
    public void Spec_pr2703_single_tx_state_check_exceeds_block_limit_rejects()
    {
        long blockGasLimit = 16_777_216 + 100; // cap + tiny headroom
        // tx.gas = blockGasLimit + intrinsic_regular + 1 -> spec inclusion check rejects on state dim.
        Transaction onlyTx = Build.A.Transaction.WithHash(TestItem.KeccakA)
            .WithGasLimit(blockGasLimit + 21_000 + 1).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, onlyTx);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] results = ResultsForCount(1);
        // Simulate execution finishing with modest actual gas (post-execution view).
        // Spec inclusion check rejects before execution even though post-execution gas would fit.
        results[0].SetResult(GasResult(block, 0, 21_000, 0));

        Assert.Throws<InvalidBlockException>(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, Task.CompletedTask, CancellationToken.None),
            "spec PR 2703 requires rejection at inclusion when tx.gas - intrinsic.regular > block_gas_limit");
    }

    /// <summary>
    /// PR 2703 <c>test_creation_tx_state_check_exceeded</c>: creation tx whose state
    /// contribution exceeds remaining state budget. Spec rejects on state dimension.
    /// Same harness-limitation as above: post-execution gas is modest, but inclusion must reject.
    /// </summary>
    [Test]
    public void Spec_pr2703_creation_tx_state_check_exceeded_rejects()
    {
        long blockGasLimit = 16_777_216 + 200_000; // cap + headroom for filler state
        Transaction filler = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216).TestObject;
        Transaction createTx = Build.A.Transaction.WithHash(TestItem.KeccakB)
            .WithCode([])
            .WithGasLimit(53_000 + (blockGasLimit - 100_000) + 1) // state contribution exceeds remaining state by 1
            .WithNonce(1).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, filler, createTx);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] results = ResultsForCount(2);
        results[0].SetResult(GasResult(block, 0, 50_000, 100_000)); // filler post-exec
        // Simulate creation tx ran with modest actual gas - spec would have rejected at inclusion.
        results[1].SetResult(GasResult(block, 1, 53_000, IntrinsicNewAccountState));

        Assert.Throws<InvalidBlockException>(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[2], null, Task.CompletedTask, CancellationToken.None),
            "spec PR 2703 requires rejection on state dimension at inclusion");
    }

    /// <summary>
    /// EIP-7825 cap: even when (tx.gas - intrinsic.state) is huge, regular worst-case is
    /// capped at TX_MAX_GAS_LIMIT. Test that IncrementalValidation correctly accepts a
    /// block where a single tx with massive headroom on regular dim still fits because
    /// post-execution actual gas is modest.
    /// </summary>
    [Test]
    public void Spec_pr2703_eip7825_cap_with_modest_actual_gas_accepts()
    {
        long blockGasLimit = 16_777_216 + 100;
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, tx);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] results = ResultsForCount(1);
        results[0].SetResult(GasResult(block, 0, 50_000, 0));

        Assert.DoesNotThrow(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, Task.CompletedTask, CancellationToken.None));
    }

    [Test]
    public void Sequential_executor_applies_eip8037_inclusion_check_before_execution()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        TestSingleReleaseSpecProvider specProvider = new(Amsterdam.Instance);
        BlockAccessListManager balManager = new(
            stateProvider,
            specProvider,
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance));

        long blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 100;
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA)
            .WithGasLimit(blockGasLimit + 21_000 + 1)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(blockGasLimit)
            .WithTransactions(tx)
            .TestObject;

        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner,
            stateProvider,
            specProvider,
            balManager,
            LimboLogs.Instance);
        BlockExecutionContext executionContext = new(block.Header, Amsterdam.Instance);
        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        executor.SetBlockExecutionContext(executionContext);
        balManager.Setup(block);

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        InvalidBlockException? ex = Assert.Throws<InvalidBlockException>(() =>
            executor.ProcessTransactions(block, ProcessingOptions.None, tracer, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("EIP-8037 inclusion check"));
    }
}
