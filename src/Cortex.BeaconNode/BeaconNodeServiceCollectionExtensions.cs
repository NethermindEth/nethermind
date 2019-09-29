using Microsoft.Extensions.DependencyInjection;

namespace Cortex.BeaconNode
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNode(this IServiceCollection services)
        {
            services.AddSingleton<BeaconChain>();
            services.AddSingleton<BeaconNodeConfiguration>();
        }
    }
}