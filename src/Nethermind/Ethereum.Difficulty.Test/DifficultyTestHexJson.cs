// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Ethereum.Difficulty.Test
{
    public class DifficultyTestHexJson
    {
        [JsonPropertyName("parentTimestamp")]
        public string ParentTimestamp { get; set; }
        [JsonPropertyName("parentDifficulty")]
        public string ParentDifficulty { get; set; }
        [JsonPropertyName("parentUncles")]
        public string ParentUncles { get; set; }
        [JsonPropertyName("currentTimestamp")]
        public string CurrentTimestamp { get; set; }
        [JsonPropertyName("currentBlockNumber")]
        public string CurrentBlockNumber { get; set; }
        [JsonPropertyName("currentDifficulty")]
        public string CurrentDifficulty { get; set; }
    }
}
