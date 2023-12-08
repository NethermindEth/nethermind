// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

using Nethermind.Core;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    [JsonConverter(typeof(ParityTraceResultConverter))]
    public class ParityTraceResult
    {
        public long GasUsed { get; set; }
        public byte[]? Output { get; set; }
        public Address? Address { get; set; }
        public byte[]? Code { get; set; }
    }
}
