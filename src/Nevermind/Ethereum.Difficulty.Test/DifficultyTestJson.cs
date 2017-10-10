using JetBrains.Annotations;

namespace Ethereum.Difficulty.Test
{
    [UsedImplicitly]
    public class DifficultyTestJson
    {
        public int ParentTimestamp { get; set; }
        public int ParentDifficulty { get; set; }
        public int CurrentTimestamp { get; set; }
        public int CurrentBlockNumber { get; set; }
        public int CurrentDifficulty { get; set; }
    }
}