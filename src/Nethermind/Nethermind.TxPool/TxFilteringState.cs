// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.TxPool;

public ref struct TxFilteringState(Transaction tx, IAccountStateProvider accounts, IReleaseSpec headSpec)
{
    private AccountStruct _senderAccount;

    /// <summary>
    /// The chain head specification the whole submission is judged against.
    /// </summary>
    /// <remarks>
    /// Captured once so that every filter, and the pool itself, agree on the rules a transaction was accepted
    /// under even if the head moves while the transaction travels through the pipeline.
    /// </remarks>
    public IReleaseSpec HeadSpec { get; } = headSpec;

    public AccountStruct SenderAccount
    {
        get
        {
            if (_senderAccount.IsNull)
            {
                accounts.TryGetAccount(tx.SenderAddress!, out _senderAccount);
            }

            return _senderAccount;
        }
    }
}
