// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.State;
namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public class GethLikeBlockNativeTracer : BlockTracerBase<GethLikeTxTrace, GethLikeNativeTxTracer>
{
    private readonly GethTraceOptions _options;
    private readonly IWorldState _worldState;
    private NativeTracerContext _context;

    public GethLikeBlockNativeTracer(IWorldState worldState, GethTraceOptions options) : base(options.TxHash)
    {
        _worldState = worldState;
        _options = options;
        _context = new NativeTracerContext();
    }

    protected override GethLikeNativeTxTracer OnStart(Transaction? tx)
    {
        _context.From = tx?.SenderAddress;
        _context.To = tx?.To;
        // _context.ExecutingAccount =
        return GethLikeNativeTracerFactory.CreateTracer(_worldState, _context, _options);
    }

    protected override bool ShouldTraceTx(Transaction? tx) => tx is not null && base.ShouldTraceTx(tx);

    protected override GethLikeTxTrace OnEnd(GethLikeNativeTxTracer txTracer) => txTracer.BuildResult();
}
