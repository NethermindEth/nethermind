// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Evm.State;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public class GethLikeBlockJavaScriptTracer(IWorldState worldState, IReleaseSpec spec, GethTraceOptions options)
    : BlockTracerBase<GethLikeTxTrace, GethLikeJavaScriptTxTracer>(options.TxHash), IDisposable
{
    private readonly Context _ctx = new();
    private readonly Db _db = new(worldState);
    private int _index;
    private GethLikeJavaScriptTxTracer? _currentTxTracer;
    private Hash256? _blockHash;
    private UInt256 _baseFee;

    public override void StartNewBlockTrace(Block block)
    {
        _ctx.block = block.Number;
        _blockHash = block.Hash;
        _baseFee = block.BaseFeePerGas;
        base.StartNewBlockTrace(block);
    }

    // Every transaction gets its own engine, released as soon as its result is built: script globals never
    // outlive a transaction and only one engine per block trace is alive at a time.
    protected override GethLikeJavaScriptTxTracer OnStart(Transaction? tx)
    {
        SetTransactionCtx(tx);
        Engine engine = new(spec);
        try
        {
            return _currentTxTracer = new GethLikeJavaScriptTxTracer(engine, _db, _ctx, options);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private void SetTransactionCtx(Transaction? tx)
    {
        _ctx.BlockHash = _blockHash;
        _ctx.GasPrice = tx!.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, _baseFee);
        _ctx.TxHash = tx.Hash;
        _ctx.txIndex = tx.Hash is not null ? _index++ : null;
        _ctx.gas = tx.GasLimit;
        _ctx.type = "CALL";
        _ctx.From = tx.SenderAddress;
        _ctx.To = tx.To;
        _ctx.Value = tx.Value;
        _ctx.Input = tx.Data;
    }

    public override void EndBlockTrace()
    {
        base.EndBlockTrace();
        Engine.CurrentEngine = null;
    }

    protected override bool ShouldTraceTx(Transaction? tx) => base.ShouldTraceTx(tx) && tx is not null;

    protected override GethLikeTxTrace OnEnd(GethLikeJavaScriptTxTracer txTracer)
    {
        GethLikeTxTrace trace = txTracer.BuildResult();
        _currentTxTracer = null;
        return trace;
    }

    public void Dispose() => Interlocked.Exchange(ref _currentTxTracer, null)?.Dispose();
}
