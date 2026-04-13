// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Data;

public readonly struct FillTransactionResult
{
    public byte[] Raw { get; init; }

    public TransactionForRpc Tx { get; init; }

    public FillTransactionResult(byte[] raw, TransactionForRpc tx)
    {
        Raw = raw;
        Tx = tx;
    }
}
