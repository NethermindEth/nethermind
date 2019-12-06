using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.BeaconNode.Storage
{
    public static class BeaconNodeStorageServiceCollectionExtensions
    {
        public static void AddBeaconNodeStorage(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IStoreProvider, MemoryStoreProvider>();
        }
    }
}
