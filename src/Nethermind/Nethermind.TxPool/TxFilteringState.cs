// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

public class TxFilteringState
{

    private readonly IAccountStateProvider _accounts;
    private readonly Transaction _tx;

    public TxFilteringState(Transaction tx, IAccountStateProvider accounts)
    {
        _accounts = accounts;
        _tx = tx;
    }

    private AccountStruct? _senderAccount = null;
    public AccountStruct SenderAccount { get { return _senderAccount ??= _accounts.GetAccount(_tx.SenderAddress!); } }
}
