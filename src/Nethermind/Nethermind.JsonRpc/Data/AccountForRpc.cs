// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data;

public readonly struct AccountForRpc
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

    public readonly ValueHash256 CodeHash => (_accountStruct?.CodeHash ?? _account?.CodeHash.ValueHash256)!.Value;
    public readonly ValueHash256 StorageRoot => (_accountStruct?.StorageRoot ?? _account?.StorageRoot.ValueHash256)!.Value;
    public readonly UInt256 Balance => (_accountStruct?.Balance ?? _account?.Balance)!.Value;
    public readonly UInt256 Nonce => (_accountStruct?.Nonce ?? _account?.Nonce)!.Value;

}
