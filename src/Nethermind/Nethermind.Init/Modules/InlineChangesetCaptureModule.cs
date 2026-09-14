// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
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
