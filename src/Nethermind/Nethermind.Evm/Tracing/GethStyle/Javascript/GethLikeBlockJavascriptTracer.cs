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
    private readonly GethJavascriptStyleCtx _ctx;
    private readonly GethJavascriptStyleDb _db;
    private int _index;

    public GethLikeBlockJavascriptTracer(IWorldState worldState, IReleaseSpec spec, GethTraceOptions options) : base(options.TxHash)
    {
        _spec = spec;
        _options = options;
        _ctx = new GethJavascriptStyleCtx();
        _db = new GethJavascriptStyleDb(worldState);
    }

    public override void StartNewBlockTrace(Block block)
    {
        _ctx.block = block.Number;
        // _ctx.blockHash = block.Hash
        base.StartNewBlockTrace(block);
    }

    protected override GethLikeJavascriptTxTracer OnStart(Transaction? tx)
    {
        _ctx.gasPrice = (ulong)tx!.GasPrice;
        _ctx.txIndex = _index++;
        return new GethLikeJavascriptTxTracer(tx.Hash!, _db, _ctx, _spec, _options);
    }

    protected override bool ShouldTraceTx(Transaction? tx) => base.ShouldTraceTx(tx) && tx is not null;

    protected override GethLikeTxTrace OnEnd(GethLikeJavascriptTxTracer txTracer) => txTracer.BuildResult();
}
