// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethLikeBlockJavascriptTracer : BlockTracerBase<GethLikeTxTrace, GethLikeJavascriptTxTracer>
{
    private readonly GethTraceOptions _options;

    public GethLikeBlockJavascriptTracer(GethTraceOptions options) : base(options.TxHash) => _options = options;

    protected override GethLikeJavascriptTxTracer OnStart(Transaction? tx) => new(_options);

    protected override GethLikeTxTrace OnEnd(GethLikeJavascriptTxTracer txTracer) => txTracer.BuildResult();

}
