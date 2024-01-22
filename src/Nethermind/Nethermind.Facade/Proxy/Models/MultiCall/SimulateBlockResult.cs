// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateBlockResult
{
    public ulong Number { get; set; }
    public Hash256 Hash { get; set; } = Keccak.Zero;
    public ulong Timestamp { get; set; }
    public ulong GasLimit { get; set; }
    public ulong GasUsed { get; set; }
    public Address FeeRecipient { get; set; } = Address.Zero;
    public UInt256 BaseFeePerGas { get; set; }
    public IEnumerable<SimulateCallResult> Calls { get; set; } = Enumerable.Empty<SimulateCallResult>();
    public byte[]? PrevRandao { get; set; }

}
