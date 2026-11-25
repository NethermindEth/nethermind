// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Eez.Execution;
using Nethermind.Int256;

namespace Nethermind.Eez.Test;

internal static class SystemTransactions
{
    public static Transaction Create(ulong chainId, ulong nonce = 0, UInt256 value = default, byte[]? data = null) => new()
    {
        Type = EezConstants.SystemTxType,
        ChainId = chainId,
        Nonce = nonce,
        To = EezConstants.Eezl2Address,
        Value = value,
        Data = data ?? Array.Empty<byte>(),
        SenderAddress = EezConstants.SystemAddress,
        GasLimit = EezConstants.SystemTxGasLimit,
    };
}
