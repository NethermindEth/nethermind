using System.Linq;
using Cortex.BeaconNode.Services;
using Cortex.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.BeaconNode.MockedStart
{
    public static class QuickStartServiceCollectionExtensions
    {
        private const ulong DefaultEth1Timestamp = 1 << 40;
        private static byte[] DefaultEth1BlockHash = Enumerable.Repeat((byte)0x42, 32).ToArray();

        public static void AddQuickStart(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<QuickStart>();
            services.Configure<QuickStartParameters>(x =>
            {
                configuration.Bind("QuickStart", section =>
                {
                    x.GenesisTime = section.GetValue<ulong>("GenesisTime");
                    x.ValidatorCount = section.GetValue<ulong>("ValidatorCount");
                    x.Eth1BlockHash = new Hash32(section.GetBytesFromPrefixedHex("Eth1BlockHash", () => DefaultEth1BlockHash));
                    x.Eth1Timestamp = section.GetValue("Eth1Timestamp", DefaultEth1Timestamp);
                    x.UseSystemClock = section.GetValue<bool>("UseSystemClock");
                });
            });

            if (!configuration.GetValue<bool>("QuickStart:UseSystemClock"))
            {
                var genesisTime = configuration.GetValue<ulong>("QuickStart:GenesisTime");
                var quickStartClock = new QuickStartClock(genesisTime);
                services.AddSingleton<IClock>(quickStartClock);
            }
        }
    }
}
