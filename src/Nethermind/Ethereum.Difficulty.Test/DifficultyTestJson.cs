// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Difficulty.Test
{
    public class DifficultyTestJson
    {
        public ulong ParentTimestamp { get; set; }
        public ulong ParentDifficulty { get; set; }
        public ulong CurrentTimestamp { get; set; }
        public ulong CurrentBlockNumber { get; set; }
        public ulong CurrentDifficulty { get; set; }
    }
}
