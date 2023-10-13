// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
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
    private readonly GethJavascriptStyleDb _db;
    // private readonly V8ScriptEngine _engine = new(V8ScriptEngineFlags.AwaitDebuggerAndPauseOnStart | V8ScriptEngineFlags.EnableDebugging);

    public GethLikeBlockJavascriptTracer(IWorldState worldState, IReleaseSpec spec, GethTraceOptions options) : base(options.TxHash)
    {
        _worldState = worldState;
        _spec = spec;
        _options = options;
        _ctx = new GethJavascriptStyleCtx();
        _db = new GethJavascriptStyleDb(worldState);
    }

    public override void StartNewBlockTrace(Block block)
    {
        _ctx.block = (ulong)block.Number;
        base.StartNewBlockTrace(block);
    }

    protected override GethLikeJavascriptTxTracer OnStart(Transaction? tx)
    {
        _ctx.gasPrice = (BigInteger?)tx?.GasPrice;
        _ctx.intrinsicGas = IntrinsicGasCalculator.Calculate(tx, _spec);
        return new GethLikeJavascriptTxTracer(_db, _ctx, _spec, _options);
    }


    protected override GethLikeTxTrace OnEnd(GethLikeJavascriptTxTracer txTracer) => txTracer.BuildResult();
}
