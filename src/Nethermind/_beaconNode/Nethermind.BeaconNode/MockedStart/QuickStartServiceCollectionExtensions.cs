//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
                    clockOffset = (long) genesisTime - DateTimeOffset.Now.ToUnixTimeSeconds();
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