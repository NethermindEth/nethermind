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
            services.AddSingleton<IEth1DataProvider, MockedEth1DataProvider>();
            services.AddSingleton<IOperationPool, MockedOperationPool>();
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
                ulong genesisTime = configuration.GetValue<ulong>("QuickStart:GenesisTime");
                QuickStartClock quickStartClock = new QuickStartClock(genesisTime);
                services.AddSingleton<IClock>(quickStartClock);
            }
        }
    }
}
