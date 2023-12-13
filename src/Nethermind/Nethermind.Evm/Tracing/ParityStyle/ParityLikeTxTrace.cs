// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class ParityLikeTxTrace
    {
        public byte[]? Output { get; set; }

        public Hash256? BlockHash { get; set; }

        [JsonConverter(typeof(LongRawJsonConverter))]
        public long BlockNumber { get; set; }

        public int? TransactionPosition { get; set; }

        public Hash256? TransactionHash { get; set; }

        public ParityVmTrace? VmTrace { get; set; }

        public ParityTraceAction? Action { get; set; }

        public Dictionary<Address, ParityAccountStateChange>? StateChanges { get; set; }
    }
}
