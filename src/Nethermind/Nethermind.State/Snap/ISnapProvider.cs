using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public interface ISnapProvider
    {
        ProgressTracker ProgressTracker { get; set; }

        bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null);
        bool AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, Keccak expectedRootHash, Keccak startingHash, PathWithStorageSlot[] slots, byte[][] proofs = null);

        ICollection<Keccak> AddCodes(Keccak[] requestedHashes, byte[][] codes);
    }
}
