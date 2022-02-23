using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public interface ISnapProvider
    {
        public bool MoreChildrenToRight { get; set; }

        //bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null);
    }
}
