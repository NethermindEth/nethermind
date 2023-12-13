// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

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
