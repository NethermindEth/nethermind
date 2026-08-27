// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public ref struct TxFilteringState(Transaction tx, IAccountStateProvider accounts)
{
    private AccountStruct _senderAccount;

    /// <summary>Whether a filter has taken this transaction's EIP-8141 paymaster slot and still owes its release.</summary>
    /// <remarks>The slot is counted before the filters that follow can reject, so the pool unwinds it once the
    /// outcome is known rather than leaving the sponsor permanently short.</remarks>
    public bool PaymasterReserved;

    /// <remarks>
    /// A failed lookup leaves the out-value undefined and the readers diverge on it, so a missing
    /// account is normalised to <see cref="AccountStruct.TotallyEmpty"/>. A zeroed code hash would
    /// otherwise read back as code-bearing, and inconsistently so once the pool's account cache
    /// answers the same address from its own empty entry.
    /// </remarks>
    public AccountStruct SenderAccount
    {
        get
        {
            if (_senderAccount.IsNull && !accounts.TryGetAccount(tx.SenderAddress!, out _senderAccount))
            {
                _senderAccount = AccountStruct.TotallyEmpty;
            }

            return _senderAccount;
        }
    }
}
