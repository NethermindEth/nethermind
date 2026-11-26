// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class DiscoveryStorageKeysTests
{
    [TestCase(DiscoveryStorageKeys.V4, DbNames.DiscoveryNodes)]
    [TestCase(DiscoveryStorageKeys.V5, DbNames.DiscoveryV5Nodes)]
    public void Storage_key_matches_the_database_the_client_registers_it_under(string storageKey, string dbName) =>
        Assert.That(storageKey, Is.EqualTo(dbName));
}
