using System.Numerics;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Rewards
{
    public abstract class SimpleRewardCalculator : IRewardCalculator {
        
        public BlockReward[] CalculateRewards(Block block)
        {
            UInt256 blockReward = GetBlockReward(block);
            BlockReward[] rewards = new BlockReward[1 + block.Ommers.Length];

            BlockHeader blockHeader = block.Header;
            BigInteger mainReward = blockReward + (uint) block.Ommers.Length * (blockReward >> 5);
            rewards[0] = new BlockReward(blockHeader.Beneficiary, mainReward);

            for (int i = 0; i < block.Ommers.Length; i++)
            {
                BigInteger ommerReward = GetOmmerReward(blockReward, blockHeader, block.Ommers[i]);
                rewards[i + 1] = new BlockReward(block.Ommers[i].Beneficiary, ommerReward, BlockRewardType.Uncle);
            }

            return rewards;
        }

        protected abstract UInt256 GetBlockReward(Block block);
        
        protected virtual UInt256 GetOmmerReward(UInt256 blockReward, BlockHeader blockHeader, BlockHeader ommer)
        {
            return blockReward - ((uint) (blockHeader.Number - ommer.Number) * blockReward >> 3);
        }
    }
}