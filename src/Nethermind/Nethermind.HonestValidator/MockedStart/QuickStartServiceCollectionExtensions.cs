// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.Services;
using Nethermind.Logging.Microsoft;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Configuration.MockedStart;

namespace Nethermind.HonestValidator.MockedStart
{
    public static class QuickStartServiceCollectionExtensions
    {
        public static void AddHonestValidatorQuickStart(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IValidatorKeyProvider, QuickStartKeyProvider>();

            services.Configure<QuickStartParameters>(x =>
            {
                // FIXME: Duplication with quickstart in beacon node... need to move configuration to Nethermind.Core2.Configuration.
                configuration.Bind("QuickStart", section =>
                {
                    x.GenesisTime = section.GetValue<ulong>("GenesisTime");
                    x.ValidatorCount = section.GetValue<ulong>("ValidatorCount");
                    //                    x.Eth1BlockHash = new Hash32(section.GetBytesFromPrefixedHex("Eth1BlockHash", () => s_defaultEth1BlockHash));
                    //                    x.Eth1Timestamp = section.GetValue("Eth1Timestamp", DefaultEth1Timestamp);
                    x.UseSystemClock = section.GetValue<bool>("UseSystemClock");
                    x.ValidatorStartIndex = section.GetValue<ulong>("ValidatorStartIndex");
                    x.NumberOfValidators = section.GetValue<ulong>("NumberOfValidators");
                    x.ClockOffset = section.GetValue<long>("ClockOffset");
                });
            });

            if (!configuration.GetValue<bool>("QuickStart:UseSystemClock"))
            {
                long clockOffset;
                if (configuration.GetSection("QuickStart:ClockOffset").Exists())
                {
                    clockOffset = configuration.GetValue<long>("QuickStart:ClockOffset");
                }
                else
                {
                    ulong genesisTime = configuration.GetValue<ulong>("QuickStart:GenesisTime");
                    clockOffset = (long)genesisTime - DateTimeOffset.Now.ToUnixTimeSeconds();
                }

                services.AddSingleton<IClock>(serviceProvider =>
                {
                    ILogger<QuickStartClock> logger = serviceProvider.GetService<ILogger<QuickStartClock>>();
                    if (logger.IsWarn()) Log.QuickStartClockCreated(logger, clockOffset, null);
                    QuickStartClock quickStartClock = new QuickStartClock(clockOffset);
                    return quickStartClock;
                });
            }
        }
    }
}
