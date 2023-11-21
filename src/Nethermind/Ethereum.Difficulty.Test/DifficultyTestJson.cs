// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Ethereum.Difficulty.Test
{
    public class DifficultyTestJson
    {
        [JsonPropertyName("parentTimestamp")]
        public int ParentTimestamp { get; set; }
        [JsonPropertyName("parentDifficulty")]
        public int ParentDifficulty { get; set; }
        [JsonPropertyName("currentTimestamp")]
        public int CurrentTimestamp { get; set; }
        [JsonPropertyName("currentBlockNumber")]
        public int CurrentBlockNumber { get; set; }
        [JsonPropertyName("currentDifficulty")]
        public int CurrentDifficulty { get; set; }
    }
}
