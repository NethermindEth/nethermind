// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public delegate GethLikeNativeTxTracer NativeTracerFactory(Block block, Transaction transaction);

public class GethLikeBlockNativeTracer : BlockTracerBase<GethLikeTxTrace, GethLikeNativeTxTracer>
{
    private readonly NativeTracerFactory _txTracerFactory;
    private Block _block = null!;

    public GethLikeBlockNativeTracer(Hash256? txHash, NativeTracerFactory txTracerFactory) : base(txHash)
    {
        _txTracerFactory = txTracerFactory;
    }

    public override void StartNewBlockTrace(Block block)
    {
        _block = block;
        base.StartNewBlockTrace(block);
    }

    protected override GethLikeNativeTxTracer OnStart(Transaction? tx) => _txTracerFactory(_block, tx!);

    protected override bool ShouldTraceTx(Transaction? tx) => tx is not null && base.ShouldTraceTx(tx);

    protected override GethLikeTxTrace OnEnd(GethLikeNativeTxTracer txTracer) => txTracer.BuildResult();
}
