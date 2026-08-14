// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;

public delegate GethLikeNativeTxTracer NativeTracerFactory(Block block, Transaction transaction);

public class GethLikeBlockNativeTracer(Hash256? txHash, NativeTracerFactory txTracerFactory) : BlockTracerBase<GethLikeTxTrace, GethLikeNativeTxTracer>(txHash)
{
    private readonly NativeTracerFactory _txTracerFactory = txTracerFactory;
    private Block _block = null!;

    public override void StartNewBlockTrace(Block block)
    {
        _block = block;
        base.StartNewBlockTrace(block);
    }

    protected override GethLikeNativeTxTracer OnStart(Transaction? tx) => _txTracerFactory(_block, tx!);

    protected override bool ShouldTraceTx(Transaction? tx) => tx is not null && base.ShouldTraceTx(tx);

    protected override GethLikeTxTrace OnEnd(GethLikeNativeTxTracer txTracer) => txTracer.BuildResult();
}
