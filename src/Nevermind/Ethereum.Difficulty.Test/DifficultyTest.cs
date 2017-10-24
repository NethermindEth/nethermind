using System.Diagnostics;
using System.Numerics;

namespace Ethereum.Difficulty.Test
{
    [DebuggerDisplay("{Name}")]
    public class DifficultyTest
    {
        public DifficultyTest(
            string fileName,
            string name,
            BigInteger parentTimestamp,
            BigInteger parentDifficulty,
            BigInteger currentTimestamp,
            ulong currentBlockNumber,
            BigInteger currentDifficulty,
            bool parentHasUncles)
        {
            Name = name;
            FileName = fileName;
            ParentTimestamp = parentTimestamp;
            ParentDifficulty = parentDifficulty;
            CurrentTimestamp = currentTimestamp;
            CurrentDifficulty = currentDifficulty;
            CurrentBlockNumber = currentBlockNumber;
            ParentHasUncles = parentHasUncles;
        }

        public BigInteger ParentTimestamp { get; set; }
        public BigInteger ParentDifficulty { get; set; }
        public BigInteger CurrentTimestamp { get; set; }
        public ulong CurrentBlockNumber { get; set; }
        public bool ParentHasUncles { get; set; }
        public BigInteger CurrentDifficulty { get; set; }
        public string Name { get; set; }
        public string FileName { get; set; }

        public override string ToString()
        {
            return string.Concat(CurrentBlockNumber, ".", CurrentTimestamp - ParentTimestamp, ".", Name);
        }
    }
}