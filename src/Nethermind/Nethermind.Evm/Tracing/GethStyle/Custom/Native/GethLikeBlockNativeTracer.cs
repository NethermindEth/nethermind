// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;
namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native;

public class GethLikeBlockNativeTracer : BlockTracerBase<GethLikeTxTrace, GethLikeNativeTxTracer>
{
    private readonly GethTraceOptions _options;
    private readonly IWorldState _worldState;
    private readonly IReleaseSpec _spec;
    private Context _context;
    private UInt256 _baseFee;
    public struct Context
    {
        public UInt256 GasPrice;
        public long GasLimit;
        public Address ContractAddress;
    }

    public GethLikeBlockNativeTracer(IWorldState worldState, IReleaseSpec spec, GethTraceOptions options) : base(options.TxHash)
    {
        _worldState = worldState;
        _options = options;
        _spec = spec;
        _context = new Context();
    }

    public override void StartNewBlockTrace(Block block)
    {
        _baseFee = block.BaseFeePerGas;
        base.StartNewBlockTrace(block);
    }

    protected override GethLikeNativeTxTracer OnStart(Transaction? tx)
    {
        _context.GasPrice = tx!.CalculateEffectiveGasPrice(_spec.IsEip1559Enabled, _baseFee);
        _context.GasLimit = tx.GasLimit;
        return GethLikeNativeTracerFactory.CreateTracer(_worldState, _context, _options);
    }

    protected override bool ShouldTraceTx(Transaction? tx) => tx is not null && base.ShouldTraceTx(tx);

    protected override GethLikeTxTrace OnEnd(GethLikeNativeTxTracer txTracer) => txTracer.BuildResult();
}
