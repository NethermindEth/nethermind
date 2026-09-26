// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Eez.Execution;

public static class EezTransactionExtensions
{
    public static bool IsEezSystemTransaction(this Transaction tx) => tx.Type == EezConstants.SystemTxType;

    /// <summary>
    /// Whether the fields the system-transaction wire format leaves implicit hold their protocol values.
    /// </summary>
    /// <remarks>
    /// The decoder always produces them; a transaction built any other way (RPC, block production, tests)
    /// must not reach execution with a different sender, target, budget or price.
    /// </remarks>
    public static bool HasSystemTransactionFields(this Transaction tx) =>
        tx.SenderAddress == EezConstants.SystemAddress
        && tx.To == EezConstants.Eezl2Address
        && tx.GasLimit == EezConstants.SystemTxGasLimit
        && tx.GasPrice.IsZero
        && tx.DecodedMaxFeePerGas.IsZero
        && tx.AccessList is null
        && tx.AuthorizationList is null
        && tx.BlobVersionedHashes is null;
}
