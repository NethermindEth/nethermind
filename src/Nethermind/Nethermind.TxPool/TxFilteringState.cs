// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.TxPool;

public ref struct TxFilteringState(Transaction tx, IAccountStateProvider accounts, IReleaseSpec headSpec)
{
    private AccountStruct _senderAccount;

    /// <summary>Set once every <c>frame_signatures</c> entry has verified against the head spec.</summary>
    /// <remarks>Lets a downstream filter assert pre-validation from what actually ran rather than from
    /// its position in the chain, so a reorder degrades to re-verifying instead of trusting a stranger.
    /// Written only inside the pool: <see cref="Filters.IIncomingTxFilter"/> is public, and an
    /// implementation outside it cannot have done the verification this claims.</remarks>
    public bool FrameSignaturesVerified { get; internal set; }

    /// <summary>Whether a filter has taken this transaction's EIP-8141 paymaster slot and still owes its release.</summary>
    /// <remarks>The slot is counted before the filters that follow can reject, so the pool unwinds it once the
    /// outcome is known rather than leaving the sponsor permanently short.</remarks>
    public bool PaymasterReserved;

    /// <summary>
    /// The chain head specification the whole submission is judged against.
    /// </summary>
    /// <remarks>
    /// Captured once so that every filter, and the pool itself, agree on the rules a transaction was accepted
    /// under even if the head moves while the transaction travels through the pipeline.
    /// </remarks>
    public IReleaseSpec HeadSpec { get; } = headSpec;

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
