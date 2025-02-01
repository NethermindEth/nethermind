using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Facade.Tracing
{
    public interface ITracerFactory<T>
    {
        IBlockTracer<T> CreateTracer(bool traceTransfers, bool returnFullTransactionObjects, ISpecProvider specProvider);
    }
}
