// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Result of <c>eth_fillTransaction</c>: the unsigned transaction with all missing
/// fields (nonce, gas, fees, chain id) populated, ready to be signed and submitted.
/// </summary>
public class FillTransactionResult
{
    // Serialized as "tx" via the camelCase naming policy - no explicit attribute needed.
    public required TransactionForRpc Tx { get; init; }
}
