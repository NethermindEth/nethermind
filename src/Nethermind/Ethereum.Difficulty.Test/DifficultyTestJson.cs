// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Difficulty.Test
{
    public class DifficultyTestJson
    {
        public int ParentTimestamp { get; set; }
        public int ParentDifficulty { get; set; }
        public int CurrentTimestamp { get; set; }
        public int CurrentBlockNumber { get; set; }
        public int CurrentDifficulty { get; set; }
    }
}
