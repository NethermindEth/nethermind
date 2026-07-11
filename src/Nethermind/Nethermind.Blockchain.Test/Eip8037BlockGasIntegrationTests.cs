// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;
using System.Threading;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Integration tests exercising EIP-8037 per-tx 2D block-gas accounting
/// against Nethermind's <c>BlockAccessListManager.IncrementalValidation</c>. Each test
/// covers the same inclusion rule on the BAL-on and BAL-off execution paths.
///
/// Tests pinned to <c>Assert.DoesNotThrow</c> verify spec acceptance; tests pinned to
/// <c>Assert.Throws&lt;InvalidBlockException&gt;</c> verify spec rejection.
///
/// </summary>
[Parallelizable(ParallelScope.All)]
public class Eip8037BlockGasIntegrationTests
{
    private const ulong Cpsb = 1530ul;
    private const ulong IntrinsicNewAccountState = 120ul * Cpsb;

    private static BlockAccessListManager CreateAmsterdamBalManager()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        return ManualBlockAccessListManagerFactory.Create(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            CodeInfoRepositoryFactories.Caching);
    }

    private static (BlockAccessListManager, Block) BuildAmsterdamBlock(ulong blockGasLimit, params Transaction[] txs)
    {
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Block block = Build.A.Block
            .WithNumber(1ul)
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
        GasResult(Block block, int txIndex, ulong blockGasUsed, ulong blockStateGasUsed, InvalidBlockException? exception = null) =>
        new(blockGasUsed, blockStateGasUsed, exception);

    // Boundary: post-tx cumulative state hits the limit exactly (must accept,
    // IncrementalValidation uses strict >) vs exceeds by 1 (must reject).
    [TestCase(200_000ul, true, TestName = "Eip8037_boundary_state_exact_fit_accepts")]
    [TestCase(200_001ul, false, TestName = "Eip8037_boundary_state_exceeded_by_one_rejects")]
    public void Eip8037_boundary_state(ulong blockStateGasUsed, bool accepts)
    {
        ulong blockGasLimit = 200_000ul;
        Transaction tx1 = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(50_000ul).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, tx1);

        GasValidationResultSlot[] results = ResultsForCount(1);
        results[0].TrySetResult(GasResult(block, 0, 50_000ul, blockStateGasUsed));

        if (accepts)
        {
            Assert.DoesNotThrow(() =>
                mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, CancellationToken.None));
            Assert.That(block.Header.GasUsed, Is.EqualTo(200_000));
        }
        else
        {
            InvalidBlockException? ex = Assert.Throws<InvalidBlockException>(() =>
                mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("Block gas limit exceeded"));
        }
    }

    /// <summary>
    /// The inclusion check reserves <c>min(TX_MAX_GAS_LIMIT, tx.gas)</c> in the regular dimension
    /// with no <c>intrinsic.state</c> subtraction, so a creation tx whose full gas fits is accepted.
    /// </summary>
    [Test]
    public void Eip8037_creation_tx_regular_check_actual_usage_modest_accepts()
    {
        ulong blockGasLimit = 16_777_216 + 53_000 + IntrinsicNewAccountState;
        Transaction filler = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216ul).TestObject;
        Transaction createTx = Build.A.Transaction.WithHash(TestItem.KeccakB)
            .WithCode([])
            .WithGasLimit(53_000 + IntrinsicNewAccountState)
            .WithNonce(1ul).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, filler, createTx);

        GasValidationResultSlot[] results = ResultsForCount(2);
        results[0].TrySetResult(GasResult(block, 0, 16_777_216, 0));
        results[1].TrySetResult(GasResult(block, 1, 53_000ul, IntrinsicNewAccountState));

        Assert.DoesNotThrow(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[2], null, CancellationToken.None));
    }

    /// <summary>
    /// A single tx whose worst-case state contribution exceeds
    /// <c>block_gas_limit</c> must be rejected at inclusion.
    ///
    /// Here we simulate Nethermind running the tx anyway and report modest actual
    /// post-execution gas (well under limit). Spec says reject; if Nethermind
    /// accepts because IncrementalValidation only sees post-execution numbers,
    /// this test FAILS - exposing the missing pre-tx inclusion check.
    /// </summary>
    [Test]
    public void Eip8037_single_tx_state_check_exceeds_block_limit_rejects()
    {
        ulong blockGasLimit = 16_777_216ul + 100ul; // cap + tiny headroom
        // tx.gas = blockGasLimit + intrinsic_regular + 1 -> spec inclusion check rejects on state dim.
        Transaction onlyTx = Build.A.Transaction.WithHash(TestItem.KeccakA)
            .WithGasLimit(blockGasLimit + 21_000ul + 1ul).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, onlyTx);

        GasValidationResultSlot[] results = ResultsForCount(1);
        // Simulate execution finishing with modest actual gas (post-execution view).
        // Spec inclusion check rejects before execution even though post-execution gas would fit.
        results[0].TrySetResult(GasResult(block, 0, 21_000ul, 0ul));

        Assert.Throws<InvalidBlockException>(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, CancellationToken.None),
            "EIP-8037 requires rejection at inclusion when tx.gas > state_gas_available (full reservation, no intrinsic subtraction)");
    }

    /// <summary>
    /// Creation tx whose state
    /// contribution exceeds remaining state budget. Spec rejects on state dimension.
    /// Same harness-limitation as above: post-execution gas is modest, but inclusion must reject.
    /// </summary>
    [Test]
    public void Eip8037_creation_tx_state_check_exceeded_rejects()
    {
        ulong blockGasLimit = 16_777_216ul + 200_000ul; // cap + headroom for filler state
        Transaction filler = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216ul).TestObject;
        Transaction createTx = Build.A.Transaction.WithHash(TestItem.KeccakB)
            .WithCode([])
            .WithGasLimit(53_000ul + (blockGasLimit - 100_000ul) + 1ul) // state contribution exceeds remaining state by 1
            .WithNonce(1ul).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, filler, createTx);

        GasValidationResultSlot[] results = ResultsForCount(2);
        results[0].TrySetResult(GasResult(block, 0, 50_000ul, 100_000ul)); // filler post-exec
        // Simulate creation tx ran with modest actual gas - spec would have rejected at inclusion.
        results[1].TrySetResult(GasResult(block, 1, 53_000ul, IntrinsicNewAccountState));

        Assert.Throws<InvalidBlockException>(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[2], null, CancellationToken.None),
            "EIP-8037 requires rejection on state dimension at inclusion");
    }

    /// <summary>
    /// EIP-7825 cap: even when (tx.gas - intrinsic.state) is huge, regular worst-case is
    /// capped at TX_MAX_GAS_LIMIT. Test that IncrementalValidation correctly accepts a
    /// block where a single tx with massive headroom on regular dim still fits because
    /// post-execution actual gas is modest.
    /// </summary>
    [Test]
    public void Eip8037_eip7825_cap_with_modest_actual_gas_accepts()
    {
        ulong blockGasLimit = 16_777_216ul + 100ul;
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).WithGasLimit(16_777_216ul).TestObject;
        (BlockAccessListManager mgr, Block block) = BuildAmsterdamBlock(blockGasLimit, tx);

        GasValidationResultSlot[] results = ResultsForCount(1);
        results[0].TrySetResult(GasResult(block, 0, 50_000ul, 0ul));

        Assert.DoesNotThrow(() =>
            mgr.IncrementalValidation(block, results, new BlockReceiptsTracer[1], null, CancellationToken.None));
    }

    [Test]
    public void Sequential_executor_applies_eip8037_inclusion_check_before_execution()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        TestSingleReleaseSpecProvider specProvider = new(Amsterdam.Instance);
        BlockAccessListManager balManager = ManualBlockAccessListManagerFactory.Create(
            stateProvider,
            specProvider,
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            CodeInfoRepositoryFactories.Caching);

        ulong blockGasLimit = Eip7825Constants.DefaultTxGasLimitCap + 100ul;
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA)
            .WithGasLimit(blockGasLimit + 21_000ul + 1ul)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1ul)
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
