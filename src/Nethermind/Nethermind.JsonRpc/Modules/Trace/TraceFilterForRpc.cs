// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Serialization.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Trace
{
    // An unknown member is invalid params, as in Parity, Reth and Besu, rather than a filter that silently ignores it.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public class TraceFilterForRpc
    {
        public BlockParameter? FromBlock { get; set; }

        public BlockParameter? ToBlock { get; set; }

        public Address[]? FromAddress { get; set; }

        public Address[]? ToAddress { get; set; }

        public TraceFilterMode Mode { get; set; }

        // after and count are JSON integers, not QUANTITYs, so the strict hex format must not reject a number here.
        [JsonConverter(typeof(NullableULongConverter))]
        public ulong? After { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        [JsonConverter(typeof(NullableULongConverter))]
        public ulong? Count { get; set; }
    }
}
