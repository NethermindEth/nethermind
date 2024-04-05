// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Test.Runner
{
    public class StateTestTxTraceResult
    {
        [JsonPropertyName("output")]
        public byte[] Output { get; set; }

        [JsonPropertyName("gasUsed")]
        public long GasUsed { get; set; }

        [JsonPropertyName("time")]
        public double Time { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }
    }
}
