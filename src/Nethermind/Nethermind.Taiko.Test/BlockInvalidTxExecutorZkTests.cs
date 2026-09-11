// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Taiko.BlockTransactionExecutors;
using Nethermind.Taiko.TaikoSpec;
using Nethermind.Taiko.ZkGas;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Unit tests for <see cref="BlockInvalidTxExecutor"/>:
/// <list type="bullet">
///   <item>Invalid transactions are silently dropped (world state rolled back).</item>
///   <item>Blob transactions are unconditionally skipped.</item>
///   <item>ZK gas block-limit: offending tx is rolled back and excluded,
///       <see cref="ZkGasMeter.IsLimitExceeded"/> is cleared, tx removed from pool.</item>
///   <item>ZK gas enforcement is skipped without <c>ProcessingOptions.ProducingBlock</c>.</item>
/// </list>
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockInvalidTxExecutorZkTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a minimal block containing the given transactions.</summary>
    private static Block MakeBlock(params Transaction[] txs) =>
        new BlockToProduce(Build.A.BlockHeader.WithNumber(1).TestObject, txs, []);

    /// <summary>Builds a valid (non-blob) transaction stub with a given nonce (for uniqueness).</summary>
    private static Transaction MakeTx(ulong nonce = 0) =>
        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(nonce).TestObject;

    /// <summary>Builds a blob transaction stub.</summary>
    private static Transaction MakeBlobTx() =>
        Build.A.Transaction.WithType(TxType.Blob).WithSenderAddress(TestItem.AddressA).TestObject;

    /// <summary>
    /// Creates a properly-initialised <see cref="BlockReceiptsTracer"/> for
    /// <paramref name="block"/>.
    /// </summary>
    private static BlockReceiptsTracer MakeReceiptsTracer(Block block)
    {
        BlockReceiptsTracer tracer = new();
        tracer.SetOtherTracer(NullBlockTracer.Instance);
        tracer.StartNewBlockTrace(block);
        return tracer;
    }

    /// <summary>Creates a <see cref="BlockExecutionContext"/> for the given block.</summary>
    private static BlockExecutionContext MakeBlockCtx(Block block) =>
        new(block.Header, Substitute.For<IReleaseSpec>());

    /// <summary>
    /// Spec provider that returns a Taiko release spec with <c>IsUnzenEnabled = true</c> for every
    /// query, so the producer-side ZK gas gate in <see cref="BlockInvalidTxExecutor"/> activates.
    /// </summary>
    private static ISpecProvider MakeUnzenSpecProvider()
    {
        ITaikoReleaseSpec spec = Substitute.For<ITaikoReleaseSpec>();
        spec.IsUnzenEnabled.Returns(true);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        return specProvider;
    }

    // ── invalid transactions ──────────────────────────────────────────────────

    /// <summary>
    /// A transaction for which Execute returns a failed result is silently dropped;
    /// only the anchor remains in the block.
    /// </summary>
    [Test]
    public void InvalidTx_IsDropped_AndDoesNotAppearInBlock()
    {
        Transaction anchor = MakeTx(0);
        Transaction bad = MakeTx(1);
        Block block = MakeBlock(anchor, bad);

        ITransactionProcessorAdapter txProcessor = Substitute.For<ITransactionProcessorAdapter>();
        txProcessor.Execute(anchor, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);
        txProcessor.Execute(bad, Arg.Any<ITxTracer>()).Returns(TransactionResult.MalformedTransaction);

        BlockReceiptsTracer receiptsTracer = MakeReceiptsTracer(block);

        BlockInvalidTxExecutor executor = new(txProcessor, Substitute.For<IWorldState>(),
            Substitute.For<ITxPool>(), LimboLogs.Instance);
        executor.SetBlockExecutionContext(MakeBlockCtx(block));

        executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);

        Assert.That(block.Transactions, Has.Length.EqualTo(1));
        Assert.That(block.Transactions[0], Is.SameAs(anchor));
    }

    /// <summary>
    /// When a transaction execution fails, the world state snapshot taken before
    /// the call is restored, preventing partial state writes.
    /// </summary>
    [Test]
    public void InvalidTx_WorldStateRestored()
    {
        Transaction anchor = MakeTx(0);
        Transaction bad = MakeTx(1);
        Block block = MakeBlock(anchor, bad);

        ITransactionProcessorAdapter txProcessor = Substitute.For<ITransactionProcessorAdapter>();
        txProcessor.Execute(anchor, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);
        txProcessor.Execute(bad, Arg.Any<ITxTracer>()).Returns(TransactionResult.MalformedTransaction);

        IWorldState worldState = Substitute.For<IWorldState>();
        BlockReceiptsTracer receiptsTracer = MakeReceiptsTracer(block);

        BlockInvalidTxExecutor executor = new(txProcessor, worldState, Substitute.For<ITxPool>(), LimboLogs.Instance);
        executor.SetBlockExecutionContext(MakeBlockCtx(block));

        executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);

        worldState.Received().Restore(Arg.Any<Snapshot>());
    }

    // ── blob transaction skipping ─────────────────────────────────────────────

    /// <summary>
    /// Blob transactions are unconditionally skipped and do not appear in the output.
    /// </summary>
    [Test]
    public void BlobTx_IsSkipped_Unconditionally()
    {
        Transaction anchor = MakeTx(0);
        Transaction blob = MakeBlobTx();
        Block block = MakeBlock(anchor, blob);

        ITransactionProcessorAdapter txProcessor = Substitute.For<ITransactionProcessorAdapter>();
        txProcessor.Execute(anchor, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);

        BlockReceiptsTracer receiptsTracer = MakeReceiptsTracer(block);

        BlockInvalidTxExecutor executor = new(txProcessor, Substitute.For<IWorldState>(),
            Substitute.For<ITxPool>(), LimboLogs.Instance);
        executor.SetBlockExecutionContext(MakeBlockCtx(block));

        executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);

        txProcessor.DidNotReceive().Execute(blob, Arg.Any<ITxTracer>());
        Assert.That(block.Transactions, Has.Length.EqualTo(1));
    }

    // ── ZK gas rollback ───────────────────────────────────────────────────────

    /// <summary>
    /// When a transaction exceeds the ZK gas block limit, it is rolled back, excluded
    /// from the block, and <see cref="ZkGasMeter.IsLimitExceeded"/> is cleared by the
    /// call to <see cref="ZkGasMeter.CancelTransaction"/>.
    /// </summary>
    [Test]
    public void ZkGasLimit_Exceeded_TxRolledBack_IsLimitExceededCleared()
    {
        Transaction anchor = MakeTx(0);
        Transaction overLimit = MakeTx(1);
        Block block = MakeBlock(anchor, overLimit);

        ZkGasMeterHolder holder = new();
        ZkGasMeter meter = new();
        holder.Meter = meter;

        ITransactionProcessorAdapter txProcessor = Substitute.For<ITransactionProcessorAdapter>();
        txProcessor.Execute(anchor, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);
        txProcessor.Execute(overLimit, Arg.Any<ITxTracer>()).Returns(ci =>
        {
            meter.ChargeOpcode(0xac, ulong.MaxValue / ushort.MaxValue);
            return TransactionResult.Ok;
        });

        IWorldState worldState = Substitute.For<IWorldState>();
        BlockReceiptsTracer receiptsTracer = MakeReceiptsTracer(block);

        BlockInvalidTxExecutor executor = new(txProcessor, worldState, Substitute.For<ITxPool>(), LimboLogs.Instance, holder, MakeUnzenSpecProvider());
        executor.SetBlockExecutionContext(MakeBlockCtx(block));

        executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);

        Assert.That(block.Transactions, Has.Length.EqualTo(1));
        Assert.That(block.Transactions[0], Is.SameAs(anchor));
        Assert.That(meter.IsLimitExceeded, Is.False, "IsLimitExceeded must not leak after rollback");
        worldState.Received().Restore(Arg.Any<Snapshot>());
    }

    /// <summary>
    /// After a ZK-gas-exceeded tx is expelled the loop breaks: subsequent
    /// transactions in the block are never attempted.
    /// </summary>
    [Test]
    public void ZkGasLimit_Exceeded_BreaksLoop_SubsequentTxNotProcessed()
    {
        Transaction anchor = MakeTx(0);
        Transaction overLimit = MakeTx(1);
        Transaction subsequent = MakeTx(2);
        Block block = MakeBlock(anchor, overLimit, subsequent);

        ZkGasMeterHolder holder = new();
        ZkGasMeter meter = new();
        holder.Meter = meter;

        ITransactionProcessorAdapter txProcessor = Substitute.For<ITransactionProcessorAdapter>();
        txProcessor.Execute(anchor, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);
        txProcessor.Execute(overLimit, Arg.Any<ITxTracer>()).Returns(ci =>
        {
            meter.ChargeOpcode(0xac, ulong.MaxValue / ushort.MaxValue);
            return TransactionResult.Ok;
        });
        txProcessor.Execute(subsequent, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);

        BlockReceiptsTracer receiptsTracer = MakeReceiptsTracer(block);

        BlockInvalidTxExecutor executor = new(txProcessor, Substitute.For<IWorldState>(),
            Substitute.For<ITxPool>(), LimboLogs.Instance, holder, MakeUnzenSpecProvider());
        executor.SetBlockExecutionContext(MakeBlockCtx(block));

        executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);

        txProcessor.DidNotReceive().Execute(subsequent, Arg.Any<ITxTracer>());
    }

    /// <summary>
    /// When the ZK gas limit is exceeded, the offending transaction hash is passed to
    /// <see cref="ITxPool.RemoveTransaction"/> so the mempool evicts it.
    /// </summary>
    [Test]
    public void ZkGasLimit_Exceeded_TxRemovedFromPool()
    {
        Transaction anchor = MakeTx(0);
        Transaction overLimit = MakeTx(1);
        Block block = MakeBlock(anchor, overLimit);

        ZkGasMeterHolder holder = new();
        ZkGasMeter meter = new();
        holder.Meter = meter;

        ITransactionProcessorAdapter txProcessor = Substitute.For<ITransactionProcessorAdapter>();
        txProcessor.Execute(anchor, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);
        txProcessor.Execute(overLimit, Arg.Any<ITxTracer>()).Returns(ci =>
        {
            meter.ChargeOpcode(0xac, ulong.MaxValue / ushort.MaxValue);
            return TransactionResult.Ok;
        });

        ITxPool txPool = Substitute.For<ITxPool>();
        BlockReceiptsTracer receiptsTracer = MakeReceiptsTracer(block);

        BlockInvalidTxExecutor executor = new(txProcessor, Substitute.For<IWorldState>(), txPool, LimboLogs.Instance, holder, MakeUnzenSpecProvider());
        executor.SetBlockExecutionContext(MakeBlockCtx(block));

        executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);

        txPool.Received(1).RemoveTransaction(overLimit.Hash);
    }

    // ── ZK enforcement disabled during validation ─────────────────────────────

    /// <summary>
    /// Without <c>ProcessingOptions.ProducingBlock</c>, ZK gas enforcement is disabled.
    /// Both transactions are executed even though <see cref="ZkGasMeter.IsLimitExceeded"/>
    /// would be set during tx2.
    /// </summary>
    [Test]
    public void ZkGas_NotEnforced_WhenNotProducingBlock()
    {
        Transaction anchor = MakeTx(0);
        Transaction tx2 = MakeTx(1);
        Block block = MakeBlock(anchor, tx2);

        ZkGasMeterHolder holder = new();
        ZkGasMeter meter = new();
        holder.Meter = meter;

        ITransactionProcessorAdapter txProcessor = Substitute.For<ITransactionProcessorAdapter>();
        txProcessor.Execute(anchor, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);
        txProcessor.Execute(tx2, Arg.Any<ITxTracer>()).Returns(ci =>
        {
            meter.ChargeOpcode(0xac, ulong.MaxValue / ushort.MaxValue);
            return TransactionResult.Ok;
        });

        BlockReceiptsTracer receiptsTracer = MakeReceiptsTracer(block);

        BlockInvalidTxExecutor executor = new(txProcessor, Substitute.For<IWorldState>(),
            Substitute.For<ITxPool>(), LimboLogs.Instance, holder);
        executor.SetBlockExecutionContext(MakeBlockCtx(block));

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        Assert.That(block.Transactions, Has.Length.EqualTo(2));
        txProcessor.Received(1).Execute(tx2, Arg.Any<ITxTracer>());
    }

    // ── post-execute limit break ──────────────────────────────────────────────

    /// <summary>
    /// When the anchor itself causes <see cref="ZkGasMeter.IsLimitExceeded"/> to be set,
    /// the anchor is preserved (it is mandatory and cannot be evicted), but subsequent
    /// transactions are skipped via the pre-loop guard at the next iteration.
    /// This mirrors alethia-reth (<c>!is_anchor_transaction</c> clause in
    /// <c>try_execute_filtered</c>, <c>crates/block/src/executor.rs</c>): the anchor
    /// must always survive so that <c>TaikoBlockValidator</c> does not silently reject
    /// the block for missing it. The block-level ZK gas check in
    /// <see cref="TaikoBlockProcessor"/> will surface the misconfiguration during
    /// validation.
    /// </summary>
    [Test]
    public void ZkGasExceededByAnchor_AnchorPreserved_Tx2NeverExecuted()
    {
        Transaction anchor = MakeTx(0);
        Transaction tx2 = MakeTx(1);
        Block block = MakeBlock(anchor, tx2);

        ZkGasMeterHolder holder = new();
        ZkGasMeter meter = new();
        holder.Meter = meter;

        ITransactionProcessorAdapter txProcessor = Substitute.For<ITransactionProcessorAdapter>();
        txProcessor.Execute(anchor, Arg.Any<ITxTracer>()).Returns(ci =>
        {
            meter.ChargeOpcode(0xac, ulong.MaxValue / ushort.MaxValue);
            return TransactionResult.Ok;
        });
        txProcessor.Execute(tx2, Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);

        IWorldState worldState = Substitute.For<IWorldState>();
        ITxPool txPool = Substitute.For<ITxPool>();
        BlockReceiptsTracer receiptsTracer = MakeReceiptsTracer(block);

        BlockInvalidTxExecutor executor = new(txProcessor, worldState, txPool, LimboLogs.Instance, holder, MakeUnzenSpecProvider());
        executor.SetBlockExecutionContext(MakeBlockCtx(block));

        executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);

        // Anchor is preserved.
        Assert.That(block.Transactions, Has.Length.EqualTo(1));
        Assert.That(block.Transactions[0], Is.SameAs(anchor));

        // tx2 is never attempted (pre-loop guard at i == 1 fires).
        txProcessor.DidNotReceive().Execute(tx2, Arg.Any<ITxTracer>());

        // Anchor must NOT be removed from the pool nor rolled back from state.
        txPool.DidNotReceive().RemoveTransaction(anchor.Hash);
        worldState.DidNotReceive().Restore(Arg.Any<Snapshot>());
    }
}
