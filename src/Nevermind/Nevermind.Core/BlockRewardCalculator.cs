using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Core
{
    public static class BlockRewardCalculator
    {
        private static readonly BigInteger BlockReward = 5.Ether();

        public static Dictionary<Address, BigInteger> CalculateRewards(Block block)
        {
            BlockHeader blockHeader = block.Header;
            Dictionary<Address, BigInteger> rewards = new Dictionary<Address, BigInteger>();
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