
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.State;

namespace Nethermind.Facade.Simulate;
public class SimulateBlockTracerFactory<TTrace>(GethTraceOptions? Options = null, ParityTraceTypes? Types = null)
{
    private readonly GethTraceOptions? _options = Options;
    private readonly ParityTraceTypes? _types = Types;

    private IBlockTracer<GethLikeTxTrace> CreateOptionsTracer(BlockHeader block, IWorldState worldState, ISpecProvider specProvider) =>
        _options switch
        {
            { Tracer: var t } when GethLikeNativeTracerFactory.IsNativeTracer(t) => new GethLikeBlockNativeTracer(_options.TxHash, (b, tx) => GethLikeNativeTracerFactory.CreateTracer(_options, b, tx, worldState)),
            { Tracer.Length: > 0 } => new GethLikeBlockJavaScriptTracer(worldState, specProvider.GetSpec(block), _options),
            _ => new GethLikeBlockMemoryTracer(_options),
        };

    public IBlockTracer<TTrace> CreateSimulateBlockTracer(
        bool isTracingLogs,
        IWorldState worldState,
        ISpecProvider spec,
        BlockHeader block)
    {
        if (_options is not null || typeof(TTrace) == typeof(GethLikeTxTrace))
        {
            return (IBlockTracer<TTrace>)CreateOptionsTracer(block, worldState, spec);
        }

        if (_types is not null || typeof(TTrace) == typeof(ParityLikeTxTrace))
        {
            return (IBlockTracer<TTrace>)new ParityLikeBlockTracer(_types ?? ParityTraceTypes.All);
        }

        return (IBlockTracer<TTrace>)new SimulateBlockMutatorTracer(isTracingLogs);
    }
}
