// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Consensus;

internal static class SystemCallFactory
{
    public const long DefaultGasLimit = 30_000_000L;

    public static SystemCall Create(Address to, UInt256 gasPrice, long gasLimit = DefaultGasLimit) =>
        Create(to, Array.Empty<byte>(), gasPrice, gasLimit);

    public static SystemCall Create(Address to, byte[] data, UInt256 gasPrice, long gasLimit = DefaultGasLimit)
    {
        SystemCall systemTx = new()
        {
            Value = UInt256.Zero,
            Data = data,
            To = to,
            SenderAddress = Address.SystemUser,
            GasLimit = gasLimit,
            GasPrice = gasPrice,
        };
        systemTx.Hash = systemTx.CalculateHash();
        return systemTx;
    }
}
