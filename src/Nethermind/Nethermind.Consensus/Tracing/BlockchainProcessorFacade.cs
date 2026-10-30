// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Tracing;

/// <summary>
/// A class to make moving from <see cref="IBlockchainProcessor"/> to <see cref="IBlockProcessor"/> simple.
/// It has same interface, but it does not change the current <see cref="IWorldState"/> which we don't want when
/// we are already managing the worldstate from outside.
/// </summary>
public sealed class BlockchainProcessorFacade
{
    private readonly IBlockProcessor _blockProcessor;
    private readonly ISpecProvider _specProvider;
    private readonly IReadOnlyList<IBlockPreprocessorStep> _preprocessorSteps;
    private readonly bool _executorRecoversTraceSenders;

    public BlockchainProcessorFacade(
        IBlockProcessor blockProcessor,
        ISpecProvider specProvider,
        IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps)
        : this(blockProcessor, specProvider, preprocessorSteps, executorRecoversTraceSenders: false)
    {
    }

    /// <param name="executorRecoversTraceSenders">
    /// Whether <paramref name="blockProcessor"/> runs a <see cref="TransactionTraceExecutor"/>, which recovers the
    /// senders of a replay bounded at one transaction just before it runs them. Only <see cref="TransactionTraceModule"/>
    /// sets it, where it installs that executor.
    /// </param>
    internal BlockchainProcessorFacade(
        IBlockProcessor blockProcessor,
        ISpecProvider specProvider,
        IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
        bool executorRecoversTraceSenders)
    {
        _blockProcessor = blockProcessor;
        _specProvider = specProvider;
        _preprocessorSteps = preprocessorSteps;
        _executorRecoversTraceSenders = executorRecoversTraceSenders;
    }

    public Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default)
    {
        bool executorRecoversSenders = _executorRecoversTraceSenders && TransactionTraceBoundary.Get(tracer, options) is not null;
        for (int i = 0; i < _preprocessorSteps.Count; i++)
        {
            if (executorRecoversSenders) _preprocessorSteps[i].RecoverDataForTransactionTrace(block);
            else _preprocessorSteps[i].RecoverData(block);
        }

        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        // Mirror BranchProcessor: a traced block runs the sequential EIP-7928 BAL path, because parallel workers
        // trace into their own pooled receipts tracers rather than the caller's tracer.
        ProcessingOptions blockOptions = tracer == NullBlockTracer.Instance
            ? options
            : options | ProcessingOptions.ForceSequentialBlockAccessList;

        (Block? processedBlock, TxReceipt[] _) = _blockProcessor.ProcessOne(block, blockOptions, tracer, spec, token);
        return processedBlock;
    }
}
