using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Test;

public class Eip7667Spec : Cancun
{
    private static IReleaseSpec _instance;

    protected Eip7667Spec()
    {
        Name = "Eip7667Spec";
        IsEip7667Enabled = true;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Eip7667Spec());
}