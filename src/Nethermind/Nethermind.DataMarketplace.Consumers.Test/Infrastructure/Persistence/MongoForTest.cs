// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence
{
    internal static class MongoForTest
    {
        public static Mongo2GoProvider TempDb;

        static MongoForTest()
        {
            NdmMongoConfig mongoConfig = new NdmMongoConfig();
            TempDb ??= new Mongo2GoProvider(mongoConfig, LimboLogs.Instance);
        }
    }
}
