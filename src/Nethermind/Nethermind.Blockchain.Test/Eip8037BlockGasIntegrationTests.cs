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
/// Integration tests for EIP-8037 (bal-devnet-6) per-tx 2D block-gas accounting against
/// Nethermind's <c>BlockAccessListManager.IncrementalValidation</c>. Each test names the
/// scenario it pins: tests asserting <c>DoesNotThrow</c> verify the inclusion check
/// accepts; tests asserting <c>Assert.Throws&lt;InvalidBlockException&gt;</c> verify it
/// rejects. The sequential executor regression at the bottom pins the same inclusion
/// rule on the BAL-off / parallel-disabled path.
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

    private static GasValidationResultSlot[] ResultsForCount(int n)
    {
        GasValidationResultSlot[] arr = new GasValidationResultSlot[n];
        for (int i = 0; i < n; i++) arr[i] = new();
        return arr;
    }

    private static GasValidationResult
        GasResult(Block block, int txIndex, long blockGasUsed, long blockStateGasUsed, InvalidBlockException? exception = null)
        => new(blockGasUsed, blockStateGasUsed, EthereumGasPolicy.CalculateIntrinsicGas(block.Transactions[txIndex], Amsterdam.Instance, block.Header.GasLimit), exception);

    /// <summary>
    /// Boundary: post-tx cumulative state gas hits the block gas limit exactly.
    /// <c>IncrementalValidation</c> compares with strict <c>&gt;</c>, so an exact-fit
    /// max(cumR, cumS) == limit must be accepted.
    /// </summary>
    [Test]
    public void State_dimension_exact_fit_at_block_gas_limit_accepts()
    {
        long blockGasLimit = 200_000;
        Transaction tx1 = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(50_000).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, tx1);

        GasValidationResultSlot[] results = ResultsForCount(1);
        // cumR = 50_000, cumS = 200_000 (exact fit). max = 200_000 == limit -> accept.
        results[0].TrySetResult(GasResult(block, 0, 50_000, 200_000));

        Assert.DoesNotThrow(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, Task.CompletedTask, CancellationToken.None));
        Assert.That(block.Header.GasUsed, Is.EqualTo(200_000));
    }

    /// <summary>
    /// Boundary: post-tx cumulative state gas exceeds the block gas limit by 1 — reject.
    /// </summary>
    [Test]
    public void State_dimension_one_over_block_gas_limit_rejects()
    {
        long blockGasLimit = 200_000;
        Transaction tx1 = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(50_000).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, tx1);

        GasValidationResultSlot[] results = ResultsForCount(1);
        // cumS = 200_001 -> max = 200_001 > 200_000 -> reject.
        results[0].TrySetResult(GasResult(block, 0, 50_000, 200_001));

        InvalidBlockException? ex = Assert.Throws<InvalidBlockException>(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, Task.CompletedTask, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("Block gas limit exceeded"));
    }

    /// <summary>
    /// A creation tx is accepted at inclusion because the regular-dimension worst case is
    /// <c>tx.gas - intrinsic.state</c>, i.e. intrinsic state is subtracted out so it cannot
    /// double-count against the regular budget. With actual post-execution gas modest (the
    /// create succeeds well under cap), <c>IncrementalValidation</c> also accepts.
    /// </summary>
    [Test]
    public void Creation_tx_intrinsic_state_excluded_from_regular_worst_case_accepts()
    {
        long blockGasLimit = 16_777_216 + 53_000 + 1; // cap + intrinsic_regular + 1
        Transaction filler = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216).TestObject;
        Transaction createTx = Build.A.Transaction.WithHash(TestItem.KeccakB)
            .WithCode([])
            .WithGasLimit(53_000 + IntrinsicNewAccountState)
            .WithNonce(1).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, filler, createTx);

        GasValidationResultSlot[] results = ResultsForCount(2);
        // Filler used full cap; create tx used modest regular + intrinsic state.
        results[0].TrySetResult(GasResult(block, 0, 16_777_216, 0));
        results[1].TrySetResult(GasResult(block, 1, 53_000, IntrinsicNewAccountState));

        Assert.DoesNotThrow(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[2], null, Task.CompletedTask, CancellationToken.None));
    }

    /// <summary>
    /// A single tx whose state-dimension worst case <c>(tx.gas - intrinsic.regular)</c>
    /// exceeds <c>block_gas_limit</c> must be rejected at inclusion, before execution.
    ///
    /// To prove inclusion does the rejecting (and not post-execution gas accounting), the
    /// test reports modest actual post-execution gas — well under the limit. If
    /// <c>IncrementalValidation</c> ever silently relied on post-execution numbers and
    /// skipped the inclusion check, this test would fail.
    /// </summary>
    [Test]
    public void Single_tx_state_worst_case_over_block_gas_limit_rejects_at_inclusion()
    {
        long blockGasLimit = 16_777_216 + 100; // cap + tiny headroom
        // tx.gas = blockGasLimit + intrinsic_regular + 1 -> spec inclusion check rejects on state dim.
        Transaction onlyTx = Build.A.Transaction.WithHash(TestItem.KeccakA)
            .WithGasLimit(blockGasLimit + 21_000 + 1).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, onlyTx);

        GasValidationResultSlot[] results = ResultsForCount(1);
        // Simulate execution finishing with modest actual gas (post-execution view).
        // Spec inclusion check rejects before execution even though post-execution gas would fit.
        results[0].TrySetResult(GasResult(block, 0, 21_000, 0));

        Assert.Throws<InvalidBlockException>(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, Task.CompletedTask, CancellationToken.None),
            "EIP-8037 requires rejection at inclusion when tx.gas - intrinsic.regular > block_gas_limit");
    }

    /// <summary>
    /// A creation tx whose state-dimension worst case exceeds the remaining state-gas
    /// budget after the previous tx must be rejected on the state dimension at inclusion.
    /// Like the previous test, post-execution gas is modest — only the inclusion check
    /// catches it.
    /// </summary>
    [Test]
    public void Creation_tx_state_worst_case_over_remaining_state_budget_rejects_at_inclusion()
    {
        long blockGasLimit = 16_777_216 + 200_000; // cap + headroom for filler state
        Transaction filler = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216).TestObject;
        Transaction createTx = Build.A.Transaction.WithHash(TestItem.KeccakB)
            .WithCode([])
            .WithGasLimit(53_000 + (blockGasLimit - 100_000) + 1) // state contribution exceeds remaining state by 1
            .WithNonce(1).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, filler, createTx);

        GasValidationResultSlot[] results = ResultsForCount(2);
        results[0].TrySetResult(GasResult(block, 0, 50_000, 100_000)); // filler post-exec
        // Simulate creation tx ran with modest actual gas - spec would have rejected at inclusion.
        results[1].TrySetResult(GasResult(block, 1, 53_000, IntrinsicNewAccountState));

        Assert.Throws<InvalidBlockException>(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[2], null, Task.CompletedTask, CancellationToken.None),
            "EIP-8037 requires rejection on the state dimension at inclusion");
    }

    /// <summary>
    /// Even when <c>tx.gas - intrinsic.state</c> is huge, EIP-7825 caps the regular-dimension
    /// worst case at <c>TX_MAX_GAS_LIMIT</c>. A single tx with massive headroom on the
    /// regular dimension still fits when post-execution actual gas is modest.
    /// </summary>
    [Test]
    public void Regular_worst_case_capped_by_eip7825_with_modest_post_exec_gas_accepts()
    {
        long blockGasLimit = 16_777_216 + 100;
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, tx);

        GasValidationResultSlot[] results = ResultsForCount(1);
        results[0].TrySetResult(GasResult(block, 0, 50_000, 0));

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
