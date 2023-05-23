// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.State.Test.Runner
{
    public class StateTestTxTraceResult
    {
        [JsonProperty("output")]
        public byte[] Output { get; set; }

        [JsonProperty("gasUsed")]
        public long GasUsed { get; set; }

        [JsonProperty("time")]
        public int Time { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }
    }
}
