// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

public class SimulateRequestState
{
    public bool Validate { get; set; }
    public UInt256? BlobBaseFeeOverride { get; set; }
}

public class SimulateBlockValidationTransactionsExecutor(
    IBlockProcessor.IBlockTransactionsExecutor baseTransactionExecutor,
    SimulateRequestState simulateState)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    private IReleaseSpec _spec;
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _spec = blockExecutionContext.Spec;
        baseTransactionExecutor.SetBlockExecutionContext(in blockExecutionContext);
    }

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer,
        CancellationToken token = default)
    {
        if (!simulateState.Validate)
        {
            processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotVerifyNonce | ProcessingOptions.NoValidation;
        }

        if (simulateState.BlobBaseFeeOverride is not null)
        {
            SetBlockExecutionContext(new BlockExecutionContext(block.Header, _spec, simulateState.BlobBaseFeeOverride.Value));
        }

        return baseTransactionExecutor.ProcessTransactions(block, processingOptions, receiptsTracer, token);
    }

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed
    {
        add => baseTransactionExecutor.TransactionProcessed += value;
        remove => baseTransactionExecutor.TransactionProcessed -= value;
    }
}

public class SimulateTransactionProcessorAdapter(ITransactionProcessor transactionProcessor, SimulateRequestState simulateRequestState) : ITransactionProcessorAdapter
{
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        return simulateRequestState.Validate ? transactionProcessor.Execute(transaction, txTracer) : transactionProcessor.Trace(transaction, txTracer);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
}

/// <summary>
/// This is an env for eth simulater. It is constructed by <see cref="SimulateReadOnlyBlocksProcessingEnvFactory"/>.
/// It is not thread safe and is meant to be reused. <see cref="Begin"/> must be called and the returned
/// <see cref="SimulateReadOnlyBlocksProcessingScope"/> must be disposed once done or there may be some memory leak.
/// </summary>
public class SimulateReadOnlyBlocksProcessingEnv(
    IWorldState worldState,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IOverridableCodeInfoRepository codeInfoRepository,
    SimulateRequestState simulateState,
    IBlockProcessor blockProcessor,
    BlockTreeOverlay blockTreeOverlay,
    IOverridableEnv overridableEnv,
    IReadOnlyDbProvider readOnlyDbProvider
)
{
    public SimulateReadOnlyBlocksProcessingScope Begin(BlockHeader? baseBlock)
    {
        blockTreeOverlay.ResetMainChain();
        IDisposable envDisposer = overridableEnv.BuildAndOverride(baseBlock, null);
        return new SimulateReadOnlyBlocksProcessingScope(
            worldState, specProvider, blockTree, codeInfoRepository, simulateState, blockProcessor, readOnlyDbProvider, envDisposer
        );
    }
}

public class SimulateReadOnlyBlocksProcessingScope(
    IWorldState worldState,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IOverridableCodeInfoRepository codeInfoRepository,
    SimulateRequestState simulateState,
    IBlockProcessor blockProcessor,
    IReadOnlyDbProvider readOnlyDbProvider,
    IDisposable overridableWorldStateCloser
) : IDisposable
{
    public IWorldState WorldState => worldState;
    public ISpecProvider SpecProvider => specProvider;
    public IBlockTree BlockTree => blockTree;
    public IOverridableCodeInfoRepository CodeInfoRepository => codeInfoRepository;
    public SimulateRequestState SimulateRequestState => simulateState;
    public IBlockProcessor BlockProcessor => blockProcessor;

    public void Dispose()
    {
        overridableWorldStateCloser.Dispose();
        readOnlyDbProvider.Dispose(); // For blocktree. The read only db has a buffer that need to be cleared.
    }
}
