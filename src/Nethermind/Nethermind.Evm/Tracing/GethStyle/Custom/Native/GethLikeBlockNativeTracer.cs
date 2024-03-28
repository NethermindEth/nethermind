// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public class GethLikeBlockNativeTracer : BlockTracerBase<GethLikeTxTrace, GethLikeNativeTxTracer>
{
    private readonly GethTraceOptions _options;

    public GethLikeBlockNativeTracer(GethTraceOptions options) : base(options.TxHash)
    {
        _options = options;
    }

    protected override GethLikeNativeTxTracer OnStart(Transaction? tx)
    {
        return GethLikeNativeTracerFactory.CreateTracer(_options);
    }

    protected override bool ShouldTraceTx(Transaction? tx) => tx is not null && base.ShouldTraceTx(tx);

    protected override GethLikeTxTrace OnEnd(GethLikeNativeTxTracer txTracer) => txTracer.BuildResult();
}
