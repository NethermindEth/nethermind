using System.Numerics;

namespace Nevermind.Blockchain.Difficulty
{
    public interface IDifficultyCalculator
    {
        BigInteger Calculate(BigInteger parentDifficulty, BigInteger parentTimestamp, BigInteger currentTimestamp, BigInteger blockNumber, bool parentHasUncles);
    }
}