// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native;
using Nethermind.State;

namespace Nethermind.Facade.Simulate;
public class GethStyleSimulateBlockTracerFactory<GethLikeTxTrace>(GethTraceOptions Options) : ISimulateBlockTracerFactory<GethLikeTxTrace>
{
    private GethLikeBlockNativeTracer CreateNativeTracer(IWorldState worldState) =>
        new(Options.TxHash, (b, tx) => GethLikeNativeTracerFactory.CreateTracer(Options, b, tx, worldState));

    private IBlockTracer<GethLikeTxTrace> CreateOptionsTracer(BlockHeader block, IWorldState worldState, ISpecProvider specProvider) =>
        Options switch
        {
            { Tracer: var t } when GethLikeNativeTracerFactory.IsNativeTracer(t) => (IBlockTracer<GethLikeTxTrace>)CreateNativeTracer(worldState),
            { Tracer.Length: > 0 } => (IBlockTracer<GethLikeTxTrace>)new GethLikeBlockJavaScriptTracer(worldState, specProvider.GetSpec(block), Options),
            _ => (IBlockTracer<GethLikeTxTrace>)new GethLikeBlockMemoryTracer(Options),
        };
    public IBlockTracer<GethLikeTxTrace> CreateSimulateBlockTracer(bool isTracingLogs, IWorldState worldState, ISpecProvider spec, BlockHeader block)
    {
        return CreateOptionsTracer(block, worldState, spec);
    }
}
