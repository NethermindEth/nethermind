// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceFilterForRpc
    {
        public BlockParameter? FromBlock { get; set; }

        public BlockParameter? ToBlock { get; set; }

        public Address[]? FromAddress { get; set; }

        public Address[]? ToAddress { get; set; }

        public int After { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? Count { get; set; }
    }
}
