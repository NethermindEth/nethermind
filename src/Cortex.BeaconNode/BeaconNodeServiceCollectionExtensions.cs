using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.BeaconNode
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNode(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<TimeParameters>(timeParameters =>
            {
                timeParameters.SlotsPerEpoch = configuration.GetValue<ulong>("SLOTS_PER_EPOCH");
            });

            services.AddSingleton<BeaconChain>();
            services.AddSingleton<BeaconNodeConfiguration>();
            services.AddSingleton<Store>();

            services.AddScoped<BlockProducer>();
        }
    }
}
