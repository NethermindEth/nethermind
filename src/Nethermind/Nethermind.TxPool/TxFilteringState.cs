// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public ref struct TxFilteringState(Transaction tx, IAccountStateProvider accounts)
{
    private AccountStruct _senderAccount;

    /// <summary>Set once every <c>frame_signatures</c> entry has verified against the head spec.</summary>
    /// <remarks>Lets a downstream filter assert pre-validation from what actually ran rather than from
    /// its position in the chain, so a reorder degrades to re-verifying instead of trusting a stranger.</remarks>
    public bool FrameSignaturesVerified { get; set; }

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
