using System.Collections.Generic;

namespace Nevermind.Core
{
    public static class BlockRewardCalculator
    {
        private static readonly decimal BlockReward = 5.Ether();

        public static Dictionary<Address, decimal> CalculateRewards(Block block)
        {
            BlockHeader blockHeader = block.Header;
            var rewards = new Dictionary<Address, decimal>();
            rewards[blockHeader.Beneficiary] = BlockReward + block.Ommers.Length * BlockReward / 32;
            foreach (BlockHeader ommerHeader in block.Ommers)
            {
                rewards[ommerHeader.Beneficiary] =
                    BlockReward + (ommerHeader.Number - blockHeader.Number) * BlockReward / 8;
            }

            return rewards;
        }
    }
}