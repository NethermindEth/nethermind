using Nethermind.Core.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class ReardCalculatorTests
    {
        [Test]
        [Ignore("To be implemented when the test framework is expanded")]
        public void Two_uncles_from_the_same_coinbase()
        {
            RewardCalculator calculator = new RewardCalculator(RopstenSpecProvider.Instance);
//            calculator.CalculateRewards()
        }
    }
}