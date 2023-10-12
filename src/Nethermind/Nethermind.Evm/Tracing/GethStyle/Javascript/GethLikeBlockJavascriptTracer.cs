// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethLikeBlockJavascriptTracer : BlockTracerBase<GethLikeTxTrace, GethLikeJavascriptTxTracer>
{
    private readonly IWorldState _worldState;
    private readonly IReleaseSpec _spec;
    private readonly GethTraceOptions _options;

    public GethLikeBlockJavascriptTracer(IWorldState worldState, IReleaseSpec spec, GethTraceOptions options) : base(options.TxHash)
    {
        _worldState = worldState;
        _spec = spec;
        _options = options;
    }

    protected override GethLikeJavascriptTxTracer OnStart(Transaction? tx)
    {
        // tx.GasPrice
        // IntrinsicGasCalculator.Calculate(tx, _spec);
        return new GethLikeJavascriptTxTracer(_worldState, _spec, _options);
    }

    protected override GethLikeTxTrace OnEnd(GethLikeJavascriptTxTracer txTracer) => txTracer.BuildResult();
}
