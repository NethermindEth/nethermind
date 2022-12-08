// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Infrastructure.Test
{
    [TestFixture]
    public class MongoProviderTests
    {
        [Test]
        public void Smoke_test()
        {
            MongoProvider mongoProvider = new MongoProvider(new NdmMongoConfig { ConnectionString = "mongodb://mongodb0.example.com:27017/admin" }, LimboLogs.Instance);
        }
    }
}
