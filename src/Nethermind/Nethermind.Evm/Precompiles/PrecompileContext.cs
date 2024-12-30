using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class PrecompileContext
    {
        public IReleaseSpec Spec { get; }
        public ISpecProvider SpecProvider { get; }
        public BlockExecutionContext BlockExecutionContext { get; }

        public PrecompileContext(IReleaseSpec spec, ISpecProvider specProvider, BlockExecutionContext blockExecutionContext)
        {
            Spec = spec;
            SpecProvider = specProvider;
            BlockExecutionContext = blockExecutionContext;
        }
    }
}