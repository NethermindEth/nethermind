// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceFilterForRpc
    {
        public BlockParameter? FromBlock { get; set; }

        public BlockParameter? ToBlock { get; set; }

        public Address[]? FromAddress { get; set; }

        public Address[]? ToAddress { get; set; }

        public int After { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public int? Count { get; set; }
    }
}
