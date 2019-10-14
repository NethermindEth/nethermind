using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.BeaconNode
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNode(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<BeaconChainParameters>(x =>
            {
                x.MinGenesisActiveValidatorCount = configuration.GetValue<int>("MIN_GENESIS_ACTIVE_VALIDATOR_COUNT");
                x.MinGenesisTime = configuration.GetValue<ulong>("MIN_GENESIS_TIME");
            });
            services.Configure<InitialValues>(x =>
            {
                x.GenesisEpoch = new Epoch(configuration.GetValue<ulong>("GENESIS_EPOCH"));
            });
            services.Configure<TimeParameters>(x =>
            {
                x.SlotsPerEpoch = configuration.GetValue<ulong>("SLOTS_PER_EPOCH");
            });
            services.Configure<MaxOperationsPerBlock>(x =>
            {
                x.MaxDeposits = configuration.GetValue<ulong>("MAX_DEPOSITS");
            });

            services.AddSingleton<BeaconChain>();
            services.AddSingleton<BeaconChainUtility>();
            services.AddSingleton<BeaconNodeConfiguration>();
            services.AddSingleton<Store>();
            services.AddSingleton<ICryptographyService, CryptographyService>();

            services.AddScoped<BlockProducer>();
        }
    }
}
