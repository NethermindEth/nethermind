using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Facade.Tracing
{
    public interface ITracerFactory<TTracer, TTrace>
    {
        TTracer CreateTracer(bool traceTransfers, bool returnFullTransactionObjects, ISpecProvider specProvider);
    }
}
