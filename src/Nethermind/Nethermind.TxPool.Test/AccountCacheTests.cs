// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using NUnit.Framework;
using AccountCache = Nethermind.TxPool.TxPool.AccountCache;

namespace Nethermind.TxPool.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class AccountCacheTests
{
    [Test]
    public void RemoveAccounts_drops_only_the_given_accounts_in_every_shard()
    {
        CountingAccounts provider = new();
        AccountCache cache = new(provider);

        // The last address byte picks the shard, so 64 addresses fill each of the 16 shards four times.
        Address[] addresses = new Address[64];
        for (byte i = 0; i < addresses.Length; i++)
        {
            byte[] bytes = new byte[Address.Size];
            bytes[0] = 1;
            bytes[^1] = i;
            addresses[i] = new Address(bytes);
            cache.TryGetAccount(addresses[i], out _);
        }

        using ArrayPoolList<AddressAsKey> changed = new(addresses.Length / 2);
        for (int i = 0; i < addresses.Length; i += 2) changed.Add(addresses[i]);
        cache.RemoveAccounts(changed);

        foreach (Address address in addresses) cache.TryGetAccount(address, out _);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < addresses.Length; i++)
            {
                Assert.That(provider.Reads[addresses[i]], Is.EqualTo(i % 2 == 0 ? 2 : 1), $"address {i}");
            }
        }
    }

    private sealed class CountingAccounts : IAccountStateProvider
    {
        public Dictionary<AddressAsKey, int> Reads { get; } = [];

        public bool TryGetAccount(Address address, out AccountStruct account)
        {
            Reads[address] = Reads.GetValueOrDefault(address) + 1;
            account = AccountStruct.TotallyEmpty;
            return true;
        }
    }
}
