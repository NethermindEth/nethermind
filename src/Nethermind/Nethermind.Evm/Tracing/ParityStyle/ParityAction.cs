// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class ParityTraceAction
    {
        public int[]? TraceAddress { get; set; }
        public string? CallType { get; set; }

        public bool IncludeInTrace { get; set; } = true;
        public bool IsPrecompiled { get; set; }
        public string? Type { get; set; }
        public string? CreationMethod { get; set; }
        public Address? From { get; set; }
        public Address? To { get; set; }
        public long Gas { get; set; }
        public UInt256 Value { get; set; }
        public byte[]? Input { get; set; }
        public ParityTraceResult? Result { get; set; } = new();
        public List<ParityTraceAction> Subtraces { get; set; } = new();

        public Address? Author { get; set; }
        public string? RewardType { get; set; }
        public string? Error { get; set; }
    }
}
