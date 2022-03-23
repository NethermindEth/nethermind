using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public interface ISnapProvider
    {
        Keccak NextAccountPath { get; set; }
        bool MoreAccountsToRight { get; set; }
        (PathWithAccount accountPath, Keccak nextSlotPath)? NextSlot { get; set; }
        ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; }

        bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null);
        bool AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, Keccak expectedRootHash, Keccak startingHash, PathWithStorageSlot[] slots, byte[][] proofs = null);
    }
}
