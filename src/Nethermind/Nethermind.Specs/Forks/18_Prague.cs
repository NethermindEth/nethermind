using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Test;

public class Prague : Cancun
{
    private static IReleaseSpec _instance;

    protected Prague()
    {
        Name = "Eip7667Spec";
        IsEip7667Enabled = true;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Prague());
}
