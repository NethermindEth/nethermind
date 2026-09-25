// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using Microsoft.ClearScript;
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
    private TracerRuntime? _runtime;
    private GethLikeJavaScriptTxTracer? _currentTxTracer;
    private Hash256? _blockHash;
    private UInt256 _baseFee;

    public override void StartNewBlockTrace(Block block)
    {
        _ctx.block = block.Number;
        _blockHash = block.Hash;
        _baseFee = block.BaseFeePerGas;
        _index = 0;
        base.StartNewBlockTrace(block);
    }

    /// <summary>
    /// Starts a transaction trace in its own engine inside the tracer's runtime, so script globals never outlive a
    /// transaction while the runtime, and the scripts compiled in it, serve every transaction the tracer traces.
    /// The engine is released as soon as the transaction's result is built, the runtime when the tracer is disposed.
    /// </summary>
    /// <remarks>
    /// The runtime outlives <see cref="EndBlockTrace"/> so that a tracer reused across blocks, as
    /// <c>debug_simulateV1</c> does for every block state call, builds one V8 isolate per request rather than one
    /// per block. The tracer's owner must therefore dispose it on every path.
    /// </remarks>
    protected override GethLikeJavaScriptTxTracer OnStart(Transaction? tx)
    {
        SetTransactionCtx(tx);
        _runtime ??= new TracerRuntime();
        Engine engine = new(spec, _runtime);
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
        _ctx.error = Undefined.Value;
        _ctx.Output = null;
        _ctx.gasUsed = 0;
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

    protected override bool ShouldTraceTx(Transaction? tx) => base.ShouldTraceTx(tx) && tx is not null;

    protected override GethLikeTxTrace OnEnd(GethLikeJavaScriptTxTracer txTracer)
    {
        GethLikeTxTrace trace = txTracer.BuildResult();
        _currentTxTracer = null;
        return trace;
    }

    public void Dispose()
    {
        _currentTxTracer?.Dispose();
        _currentTxTracer = null;
        _runtime?.Dispose();
        _runtime = null;
    }
}
