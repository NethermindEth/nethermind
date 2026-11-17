// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data;

public readonly struct AccountForRpc(in AccountStruct account)
{
    private readonly AccountStruct _account = account;

    public readonly ValueHash256 CodeHash => _account.CodeHash;
    public readonly ValueHash256 StorageRoot => _account.StorageRoot;
    public readonly UInt256 Balance => _account.Balance;
    public readonly UInt256 Nonce => _account.Nonce;

}
