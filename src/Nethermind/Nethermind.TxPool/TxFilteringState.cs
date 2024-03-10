// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class TxFilteringState(Transaction tx, IAccountStateProvider accounts)
{
    private AccountStruct _senderAccount;

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
