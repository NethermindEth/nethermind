using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public interface ISnapProvider
    {
        public bool MoreAccountsToRight { get; set; }
        ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; }

        //bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null);
    }
}
