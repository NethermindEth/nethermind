using Nethermind.Blockchain;

namespace Nethermind.Core.Test.Builders
{
    public static class BlockTreeExtensions
    {
        public static void AddBranch(this BlockTree blockTree, int branchLength, int splitBlockNumber, int splitVariant)
        {
            BlockTree alternative = Build.A.BlockTree(blockTree.GenesisBlock).OfChainLength(branchLength, splitBlockNumber, splitVariant).TestObject;
            for (int i = splitBlockNumber + 1; i < branchLength; i++)
            {
                Block block = alternative.FindBlock(i);
                blockTree.SuggestBlock(block);
                blockTree.MarkAsProcessed(block.Hash);
                if (branchLength > blockTree.HeadBlock.Number)
                {
                    blockTree.MoveToMain(block.Hash);    
                }
            }
        }
    }
}