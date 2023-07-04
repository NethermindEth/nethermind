// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class ParityAccountStateChange
    {
        public ParityStateChange<byte[]> Code { get; set; }
        public ParityStateChange<UInt256?> Balance { get; set; }
        public ParityStateChange<UInt256?> Nonce { get; set; }
        public Dictionary<UInt256, ParityStateChange<byte[]>> Storage { get; set; }
    }
}
