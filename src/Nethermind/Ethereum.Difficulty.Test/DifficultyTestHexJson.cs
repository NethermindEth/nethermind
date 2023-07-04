// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Difficulty.Test
{
    public class DifficultyTestHexJson
    {
        public string ParentTimestamp { get; set; }
        public string ParentDifficulty { get; set; }
        public string ParentUncles { get; set; }
        public string CurrentTimestamp { get; set; }
        public string CurrentBlockNumber { get; set; }
        public string CurrentDifficulty { get; set; }
    }
}
