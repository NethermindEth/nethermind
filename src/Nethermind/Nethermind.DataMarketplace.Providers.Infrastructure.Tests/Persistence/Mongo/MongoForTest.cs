// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Tests.Persistence.Mongo
{
    internal static class MongoForTest
    {
        public static Mongo2GoProvider Provider;

        static MongoForTest()
        {
            NdmMongoConfig mongoConfig = new NdmMongoConfig();
            Provider ??= new Mongo2GoProvider(mongoConfig, LimboLogs.Instance);
        }
    }
}
