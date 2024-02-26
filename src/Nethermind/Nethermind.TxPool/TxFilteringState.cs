// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class TxFilteringState(Transaction tx, IAccountStateProvider accounts)
{
    private static readonly AccountStruct _defaultAccount = default;
    private AccountStruct _senderAccount = _defaultAccount;

    public AccountStruct SenderAccount
    {
        get
        {
            if (_senderAccount == _defaultAccount)
            {
                accounts.TryGetAccount(tx.SenderAddress!, out _senderAccount);
            }

            return _senderAccount;
        }
    }
}
