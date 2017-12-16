using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Extensions;
using Nevermind.Core.Potocol;

namespace Nevermind.Blockchain
{
    public class RewardCalculator : IRewardCalculator
    {
        private readonly IProtocolSpecification _protocolSpecification;

        public RewardCalculator(IProtocolSpecification protocolSpecification)
        {
            _protocolSpecification = protocolSpecification;
        }
       
        public Dictionary<Address, BigInteger> CalculateRewards(Block block)
        {
            BigInteger blockReward = 5.Ether();
            if (_protocolSpecification.IsEip649Enabled)
            {
                blockReward = 3.Ether();
            }
            
            BlockHeader blockHeader = block.Header;
            Dictionary<Address, BigInteger> rewards = new Dictionary<Address, BigInteger>();
            rewards[blockHeader.Beneficiary] = blockReward + block.Ommers.Length * blockReward / 32;
            foreach (BlockHeader ommerHeader in block.Ommers)
            {
                rewards[ommerHeader.Beneficiary] =
                    blockReward + (ommerHeader.Number - blockHeader.Number) * blockReward / 8;
            }

            return rewards;
        }
    }
}