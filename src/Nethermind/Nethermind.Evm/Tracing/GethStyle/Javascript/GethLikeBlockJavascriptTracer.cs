// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethLikeBlockJavascriptTracer : BlockTracerBase<GethLikeTxTrace, GethLikeJavascriptTxTracer>
{
    private readonly IReleaseSpec _spec;
    private readonly GethTraceOptions _options;
    private readonly Context _ctx;
    private readonly Db _db;
    private int _index;

    public GethLikeBlockJavascriptTracer(IWorldState worldState, IReleaseSpec spec, GethTraceOptions options) : base(options.TxHash)
    {
        _spec = spec;
        _options = options;
        _ctx = new Context();
        _db = new Db(worldState);
    }

    public override void StartNewBlockTrace(Block block)
    {
        _ctx.block = block.Number;
        _ctx.BlockHash = block.Hash;
        base.StartNewBlockTrace(block);
    }

    protected override GethLikeJavascriptTxTracer OnStart(Transaction? tx)
    {
        V8ScriptEngine engine = new();
        JavascriptConverter.CurrentEngine = new Engine(engine, _spec);

        _ctx.gasPrice = (ulong)tx!.GasPrice;
        _ctx.TxHash = tx.Hash;
        _ctx.txIndex = tx.Hash is not null ? _index++ : null;
        return new GethLikeJavascriptTxTracer(engine, _db, _ctx, _options);
    }

    public override void EndBlockTrace()
    {
        base.EndBlockTrace();
        JavascriptConverter.CurrentEngine = null;
    }

    protected override bool ShouldTraceTx(Transaction? tx) => base.ShouldTraceTx(tx) && tx is not null;

    protected override GethLikeTxTrace OnEnd(GethLikeJavascriptTxTracer txTracer) => txTracer.BuildResult();
}
