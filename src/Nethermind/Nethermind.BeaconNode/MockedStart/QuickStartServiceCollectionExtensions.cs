// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2;
using Nethermind.Core2.Configuration.MockedStart;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.MockedStart
{
    public static class QuickStartServiceCollectionExtensions
    {
        public static void AddBeaconNodeQuickStart(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IOperationPool, MockedOperationPool>();

            if (!configuration.GetValue<bool>("QuickStart:UseSystemClock"))
            {
                long clockOffset = 0L;
                if (configuration.GetSection("QuickStart:ClockOffset").Exists())
                {
                    clockOffset = configuration.GetValue<long>("QuickStart:ClockOffset");
                }
                else if (configuration.GetSection("QuickStart:GenesisTime").Exists())
                {
                    ulong genesisTime = configuration.GetValue<ulong>("QuickStart:GenesisTime");
                    clockOffset = (long)genesisTime - DateTimeOffset.Now.ToUnixTimeSeconds();
                }

                if (clockOffset != 0)
                {
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
}
