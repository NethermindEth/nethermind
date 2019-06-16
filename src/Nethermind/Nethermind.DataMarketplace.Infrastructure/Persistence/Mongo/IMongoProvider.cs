using MongoDB.Driver;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public interface IMongoProvider
    {
        IMongoDatabase GetDatabase();
    }
}