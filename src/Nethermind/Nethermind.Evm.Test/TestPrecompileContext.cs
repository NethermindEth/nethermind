using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Test;

public class TestPrecompileContext : PrecompileContext
{
    public static TestPrecompileContext Instance { get; } = new(Prague.Instance, TestSpecProvider.Instance, new());

    private TestPrecompileContext(IReleaseSpec spec, ISpecProvider specProvider, ExecutionEnvironment executionEnvironment)
        : base(spec, specProvider, executionEnvironment)
    {
    }
}
