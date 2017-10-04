using System.Numerics;

namespace Nevermind.Core
{
    public interface IDifficultyCalculator
    {
        BigInteger Calculate(BigInteger parentDifficulty, BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber);
    }
}