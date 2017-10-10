using JetBrains.Annotations;

namespace Ethereum.Difficulty.Test
{
    [UsedImplicitly]
    public class DifficultyTestHexJson
    {
        public string ParentTimestamp { get; set; }
        public string ParentDifficulty { get; set; }
        public string CurrentTimestamp { get; set; }
        public string CurrentBlockNumber { get; set; }
        public string CurrentDifficulty { get; set; }
    }
}