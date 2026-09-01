// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeBlockMemoryTracer(GethTraceOptions options, long destroyRefund = 0)
    : BlockTracerBase<GethLikeTxTrace, GethLikeTxMemoryTracer>(options.TxHash)
{
    private readonly ISpecProvider? _specProvider;
    private long _destroyRefund = destroyRefund;

    /// <summary>
    /// Creates a tracer that selects the self-destruct refund from the active specification for each block.
    /// </summary>
    /// <param name="options">Geth trace configuration.</param>
    /// <param name="specProvider">Provider used to resolve the active block specification.</param>
    public GethLikeBlockMemoryTracer(GethTraceOptions options, ISpecProvider specProvider) : this(options)
        => _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));

    /// <inheritdoc/>
    public override void StartNewBlockTrace(Block block)
    {
        base.StartNewBlockTrace(block);

        if (_specProvider is not null)
            _destroyRefund = (long)_specProvider.GetSpec(block.Header).GasCosts.DestroyRefund;
    }

    protected override GethLikeTxMemoryTracer OnStart(Transaction? tx) => new(tx, options, _destroyRefund);

    protected override GethLikeTxTrace OnEnd(GethLikeTxMemoryTracer txTracer) => txTracer.BuildResult();
}
