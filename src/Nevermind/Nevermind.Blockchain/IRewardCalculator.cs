using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;

namespace Nevermind.Blockchain
{
    public interface IRewardCalculator
    {
        Dictionary<Address, BigInteger> CalculateRewards(Block block);
    }
}