// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class BlockOverride
{
    public Keccak PrevRandao { get; set; } = Keccak.Zero;
    public UInt256 Number { get; set; }
    public UInt256 Time { get; set; }
    public UInt64 GasLimit { get; set; }
    public Address FeeRecipient { get; set; }
    public UInt256 BaseFee { get; set; }
}
