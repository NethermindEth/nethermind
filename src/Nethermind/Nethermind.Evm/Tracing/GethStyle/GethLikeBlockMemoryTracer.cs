// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeBlockMemoryTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxMemoryTracer>
{
    private readonly GethTraceOptions _options;

    public GethLikeBlockMemoryTracer(GethTraceOptions options) : base(options.TxHash) => _options = options;

    protected override GethLikeTxMemoryTracer OnStart(Transaction? tx) => new(_options);

    protected override GethLikeTxTrace OnEnd(GethLikeTxMemoryTracer txTracer) => txTracer.BuildResult();
}
