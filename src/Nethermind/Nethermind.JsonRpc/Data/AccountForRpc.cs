// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data;

public class AccountForRpc
{
    private Account _Account { get; set; }
    public AccountForRpc(Account account)
    {
        _Account = account;
    }

    public Keccak CodeHash => _Account.CodeHash;
    public Keccak StorageRoot => _Account.StorageRoot;
    public UInt256 Balance => _Account.Balance;
    public UInt256 Nonce => _Account.Nonce;

}
