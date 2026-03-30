// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Caching;

internal static class CacheTestData
{
    internal static (AddressAsKey[] Keys, Account[] Accounts) Build(int count)
    {
        AddressAsKey[] keys = new AddressAsKey[count];
        Account[] accounts = new Account[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = Builders.Build.An.Address.FromNumber(i).TestObject;
            accounts[i] = Builders.Build.An.Account.WithBalance((UInt256)i).TestObject;
        }
        return (keys, accounts);
    }
}
