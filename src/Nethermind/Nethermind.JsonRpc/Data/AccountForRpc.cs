// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data;

public struct AccountForRpc
{
    private readonly Account? _account;
    private readonly AccountStruct? _accountStruct;
    public AccountForRpc(Account account)
    {
        _account = account;
    }

    public AccountForRpc(in AccountStruct? account)
    {
        _accountStruct = account;
    }

    public ValueHash256 CodeHash => (_accountStruct?.CodeHash ?? _account?.CodeHash.ValueHash256)!.Value;
    public ValueHash256 StorageRoot => (_accountStruct?.StorageRoot ?? _account?.StorageRoot.ValueHash256)!.Value;
    public UInt256 Balance => (_accountStruct?.Balance ?? _account?.Balance)!.Value;
    public UInt256 Nonce => (_accountStruct?.Nonce ?? _account?.Nonce)!.Value;

}
