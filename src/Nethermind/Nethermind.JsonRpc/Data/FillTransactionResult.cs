// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Data;

/// <summary>Result of <c>eth_fillTransaction</c>: the filled unsigned transaction and its RLP encoding.</summary>
public readonly struct FillTransactionResult
{
    /// <summary>Raw unsigned RLP-encoded transaction.</summary>
    public byte[] Raw { get; init; }

    /// <summary>Transaction with all fields filled in.</summary>
    public TransactionForRpc Tx { get; init; }

    public FillTransactionResult(byte[] raw, TransactionForRpc tx)
    {
        Raw = raw;
        Tx = tx;
    }
}
