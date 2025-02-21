using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.Facade.Tracing;

public class ParityLikeBlockTracerFactory(ParityTraceTypes types)
    : ITracerFactory<TraceSimulateTracer<ParityLikeTxTrace>, SimulateBlockResult<ParityLikeTxTrace>>
{
    public TraceSimulateTracer<ParityLikeTxTrace> CreateTracer(
        bool traceTransfers,
        bool returnFullTransactionObjects,
        ISpecProvider specProvider)
    {
        return new TraceSimulateTracer<ParityLikeTxTrace>(
            new ParityLikeBlockTracer(types), 
            specProvider
        );
    }
}
