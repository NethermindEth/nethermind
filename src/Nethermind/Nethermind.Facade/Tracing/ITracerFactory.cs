using Nethermind.Consensus.Tracing;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Facade.Tracing
{
    public interface ITracerFactory<out TTracer, TTrace> where TTracer : IBlockTracer<TTrace>
    {
        TTracer CreateTracer(bool traceTransfers, bool returnFullTransactionObjects, ISpecProvider specProvider);
    }
}
