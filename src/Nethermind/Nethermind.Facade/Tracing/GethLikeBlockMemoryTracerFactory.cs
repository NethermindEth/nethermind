using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.GethStyle;

namespace Nethermind.Facade.Tracing;

public class GethLikeBlockMemoryTracerFactory(GethTraceOptions options)
    : ITracerFactory<TraceSimulateTracer<GethLikeTxTrace>, SimulateBlockResult<GethLikeTxTrace>>
{
    public TraceSimulateTracer<GethLikeTxTrace> CreateTracer(
        bool traceTransfers,
        bool returnFullTransactionObjects,
        ISpecProvider specProvider)
    {
        return new TraceSimulateTracer<GethLikeTxTrace>(
            new GethLikeBlockMemoryTracer(options), 
            specProvider
        );
    }
}