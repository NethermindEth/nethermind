// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class BlockOverride
{
    public ulong? Number { get; set; }
    public Hash256 PrevRandao { get; set; } = Keccak.Zero;
    public ulong? Time { get; set; }
    public ulong? GasLimit { get; set; }
    public Address? FeeRecipient { get; set; }
    public UInt256? BaseFeePerGas { get; set; }
    public UInt256? BlobBaseFee { get; set; }

    public void ApplyOverrides(BlockHeader result)
    {
        if (Time is not null) result.Timestamp = Time.Value;
        if (GasLimit is not null)
        {
            if (GasLimit > long.MaxValue)
            {
                throw new OverflowException($"GasLimit value is too large, max value {ulong.MaxValue}");
            }
            result.GasLimit = (long)GasLimit.Value;
        }

        if (Number is not null) result.Number = (long)Number.Value;
        if (FeeRecipient is not null) result.Beneficiary = FeeRecipient;
        if (BaseFeePerGas is not null) result.BaseFeePerGas = BaseFeePerGas.Value;
        if (PrevRandao is not null && PrevRandao != Hash256.Zero) result.MixHash = PrevRandao;
    }
}
