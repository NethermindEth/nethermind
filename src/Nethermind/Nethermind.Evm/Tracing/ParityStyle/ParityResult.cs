// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Newtonsoft.Json;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class ParityTraceResult
    {
        public long GasUsed { get; set; }
        public byte[]? Output { get; set; }
        public Address? Address { get; set; }
        public byte[]? Code { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Output is null && GasUsed == 0 && Address is null && Code is null;
    }
}
