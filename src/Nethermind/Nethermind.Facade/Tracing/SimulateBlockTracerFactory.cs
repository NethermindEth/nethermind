using Nethermind.Facade;
using Nethermind.Facade.Simulate;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Core.Specs;

namespace Nethermind.Facade.Tracing;

public class SimulateBlockTracerFactory 
    : ITracerFactory<SimulateBlockTracer, SimulateBlockResult<SimulateCallResult>>
{
    public SimulateBlockTracer CreateTracer(
        bool traceTransfers,
        bool returnFullTransactionObjects,
        ISpecProvider specProvider)
    {
        return new SimulateBlockTracer(
            traceTransfers, 
            returnFullTransactionObjects, 
            specProvider
        );
    }
}