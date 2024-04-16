// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public delegate GethLikeNativeTxTracer NaviteTracerFactory(Block block, Transaction transaction);

public class GethLikeBlockNativeTracer : BlockTracerBase<GethLikeTxTrace, GethLikeNativeTxTracer>
{
    private readonly NaviteTracerFactory _txTracerFactory;
    private Block _block = null!;

    public GethLikeBlockNativeTracer(Hash256? txHash, NaviteTracerFactory txTracerFactory) : base(txHash)
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
