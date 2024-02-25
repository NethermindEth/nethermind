// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool;

public class TxFilteringState(Transaction tx, IAccountStateProvider accounts)
{
    private AccountStruct _senderAccount = AccountStruct.TotallyEmpty;

    public AccountStruct SenderAccount
    {
        get
        {
            if (_senderAccount.IsTotallyEmpty)
            {
                accounts.TryGetAccount(tx.SenderAddress!, out _senderAccount);
            }

            return _senderAccount;
        }
    }
}
