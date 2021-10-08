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
                    clockOffset = (long) genesisTime - DateTimeOffset.Now.ToUnixTimeSeconds();
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
