using Nethermind.DataMarketplace.Consumers.Test.Infrastructure.Persistence;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Tests.Persistence.Mongo
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