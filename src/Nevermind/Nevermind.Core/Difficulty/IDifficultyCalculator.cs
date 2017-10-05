using System.Numerics;

namespace Nevermind.Core.Difficulty
{
    public interface IDifficultyCalculator
    {
        BigInteger Calculate(BigInteger parentDifficulty, BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber);
    }
}