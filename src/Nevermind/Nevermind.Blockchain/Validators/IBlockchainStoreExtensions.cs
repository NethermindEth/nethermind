using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public static class IBlockchainStoreExtensions
    {
        public static Block FindParent(this IBlockStore store, BlockHeader header)
        {
            return store.FindBlock(header.ParentHash, false);
        }

        public static Block FindParent(this IBlockStore store, Block block)
        {
            return store.FindBlock(block.Header.ParentHash, false);
        }
    }
}