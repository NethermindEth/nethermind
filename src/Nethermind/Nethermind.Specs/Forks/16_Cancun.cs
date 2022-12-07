using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Cancun : Shanghai
{

    private static IReleaseSpec _instance;

    protected Cancun()
    {
        Name = "Cancun";
        IsVerkleTreeEipEnabled = true;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Cancun());

}
