// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Admits an <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> transaction that selects
/// protocol-managed nonce domains, replacing the account-nonce checks that do not apply to it.
/// </summary>
/// <remarks>
/// A keyed set carries no ordering: the spec requires <c>nonce_seq</c> to equal the current sequence of
/// every selected key, so there is no such thing as a future or gapped keyed transaction and the ordering
/// filters are skipped for one. Rejection is exact-match rather than a lower bound, which is why a keyed
/// transaction cannot reuse <see cref="AcceptTxResult.OldNonce"/>.
/// </remarks>
internal sealed class KeyedNonceFilter(IReadOnlyStateProvider stateProvider) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if (tx.NonceKeys is not { } nonceKeys || !KeyedNonceManager.UsesKeyedDomain(nonceKeys))
        {
            return AcceptTxResult.Accepted;
        }

        if (!KeyedNonceManager.IsNonceSetValid(stateProvider, tx.SenderAddress!, nonceKeys, tx.Nonce))
        {
            Metrics.PendingTransactionsKeyedNonceUnmet++;
            return AcceptTxResult.KeyedNonceUnmet;
        }

        return AcceptTxResult.Accepted;
    }

    /// <summary>Whether <paramref name="tx"/>'s replay protection lives in <c>NONCE_MANAGER</c> rather than the sender's account nonce.</summary>
    /// <remarks>
    /// The account-nonce filters and the sender bucket's nonce ordering are meaningless for such a transaction:
    /// its sender may be a contract whose account nonce is unrelated to the sequence it consumes.
    /// </remarks>
    public static bool UsesKeyedNonce(Transaction tx) =>
        tx.NonceKeys is { } nonceKeys && KeyedNonceManager.UsesKeyedDomain(nonceKeys);
}
