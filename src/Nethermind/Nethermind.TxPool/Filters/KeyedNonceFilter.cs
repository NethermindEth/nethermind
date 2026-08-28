// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.TxPool.Filters;

/// <summary>Admits an EIP-8250 transaction selecting protocol-managed nonce domains, replacing the account-nonce checks that do not apply to it.</summary>
/// <remarks><c>nonce_seq</c> must equal every selected key's current sequence, so a keyed transaction is never future or gapped and never <see cref="AcceptTxResult.OldNonce"/>.</remarks>
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
}
