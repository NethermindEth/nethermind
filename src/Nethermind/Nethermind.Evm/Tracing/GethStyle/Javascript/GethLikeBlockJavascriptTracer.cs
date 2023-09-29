// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethLikeBlockJavascriptTracer : BlockTracerBase<GethLikeTxTrace, GethLikeJavascriptTxTracer>
{
    private readonly IWorldState _worldState;
    private readonly GethTraceOptions _options;

    public GethLikeBlockJavascriptTracer(IWorldState worldState, GethTraceOptions options) : base(options.TxHash)
    {
        _worldState = worldState;
        _options = options;
    }

    protected override GethLikeJavascriptTxTracer OnStart(Transaction? tx) => new(_worldState, _options);

    protected override GethLikeTxTrace OnEnd(GethLikeJavascriptTxTracer txTracer) => txTracer.BuildResult();

}
