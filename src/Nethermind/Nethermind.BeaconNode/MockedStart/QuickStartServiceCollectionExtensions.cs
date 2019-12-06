using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.MockedStart
{
    public static class QuickStartServiceCollectionExtensions
    {
        private const ulong DefaultEth1Timestamp = (ulong)1 << 40;
        private static readonly byte[] s_defaultEth1BlockHash = Enumerable.Repeat((byte)0x42, 32).ToArray();

        public static void AddQuickStart(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<INodeStart, QuickStart>();
            services.Configure<QuickStartParameters>(x =>
            {
                configuration.Bind("QuickStart", section =>
                {
                    x.GenesisTime = section.GetValue<ulong>("GenesisTime");
                    x.ValidatorCount = section.GetValue<ulong>("ValidatorCount");
                    x.Eth1BlockHash = new Hash32(section.GetBytesFromPrefixedHex("Eth1BlockHash", () => s_defaultEth1BlockHash));
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
