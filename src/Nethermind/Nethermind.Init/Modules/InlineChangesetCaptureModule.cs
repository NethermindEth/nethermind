// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State.Flat.History.Changesets;

namespace Nethermind.Init.Modules;

/// <summary>Attaches the inline changeset capture to main block processing only. Attaching below the branch processor
/// leaves the processing options as they were, so the presence of the capture never forces a block onto the
/// sequential path; the capture itself decides per block whether it is far enough from the tip to record.</summary>
public sealed class InlineChangesetCaptureModule : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder) =>
        builder.AddDecorator<IBlockProcessor>((ctx, inner) => new InlineCaptureBlockProcessor(inner, ctx.Resolve<InlineChangesetCapture>()));
}

/// <summary>Captures a block only once it is far enough below the best header that no reorg reaches it, and only on
/// forks the index can serve at all.</summary>
public sealed class InlineCapturePolicy(IBlockTree blockTree, ISpecProvider specProvider) : IInlineCapturePolicy
{
    /// <summary>A block this far below the best header the node knows of is sync, not the tip: no reorg reaches it.</summary>
    internal const ulong TipDistance = 256;

    public bool ShouldCapture(Block block) =>
        !specProvider.GetSpec(block.Header).BlockLevelAccessListsEnabled
        && blockTree.BestSuggestedHeader is { } best
        && best.Number >= block.Number + TipDistance;
}

public sealed class InlineCaptureBlockProcessor(IBlockProcessor inner, InlineChangesetCapture capture) : IBlockProcessor
{
    public event Action? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
    {
        if (options.ContainsFlag(ProcessingOptions.ReadOnlyChain) || options.ContainsFlag(ProcessingOptions.ProducingBlock))
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

        return inner.ProcessOne(suggestedBlock, options, blockTracer == NullBlockTracer.Instance ? capture : Both(blockTracer, capture), spec, token);
    }

    private static CompositeBlockTracer Both(IBlockTracer first, IBlockTracer second)
    {
        CompositeBlockTracer composite = new();
        composite.AddRange(first, second);
        return composite;
    }
}
