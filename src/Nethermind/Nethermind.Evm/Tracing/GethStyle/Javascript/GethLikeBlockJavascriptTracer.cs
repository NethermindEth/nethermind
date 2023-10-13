// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethLikeBlockJavascriptTracer : BlockTracerBase<GethLikeTxTrace, GethLikeJavascriptTxTracer>
{
    private readonly IWorldState _worldState;
    private readonly IReleaseSpec _spec;
    private readonly GethTraceOptions _options;
    private readonly V8ScriptEngine _engine = new();
    private readonly GethJavascriptStyleCtx _ctx;

    public GethLikeBlockJavascriptTracer(IWorldState worldState, IReleaseSpec spec, GethTraceOptions options) : base(options.TxHash)
    {
        _worldState = worldState;
        _spec = spec;
        _options = options;
        _ctx = new GethJavascriptStyleCtx(_engine);
    }

    protected override GethLikeJavascriptTxTracer OnStart(Transaction? tx)
    {
        _ctx.gasPrice = tx?.GasPrice.ToInt64(null);
        _ctx.intrinsicGas = IntrinsicGasCalculator.Calculate(tx, _spec);
        return new GethLikeJavascriptTxTracer(_worldState, _spec, _options);
    }



    protected override GethLikeTxTrace OnEnd(GethLikeJavascriptTxTracer txTracer) => txTracer.BuildResult();
}
