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
    public List<SimulateCallResult> Calls { get; set; } = new();
    public byte[]? PrevRandao { get; set; }
    public Withdrawal[] Withdrawals { get; set; }

    public ulong BlobGasUsed { get; set; }
    public UInt256 ExcessBlobGas { get; set; }
    public UInt256 BlobBaseFee { get; set; }

}
