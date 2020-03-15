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
using Nethermind.BeaconNode.Eth1Bridge.MockedStart;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Eth1Bridge
{
    public static class Eth1BridgeServiceCollectionExtensions
    {
        private static readonly byte[] DefaultEth1BlockHash = Enumerable.Repeat((byte) 0x42, 32).ToArray();

        public static void AddBeaconNodeEth1Bridge(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddHostedService<Eth1BridgeWorker>();

            if (configuration.GetSection("QuickStart").Exists())
            {
                services.Configure<QuickStartParameters>(x =>
                {
                    configuration.Bind("QuickStart", section =>
                    {
                        x.GenesisTime = section.GetValue<ulong>("GenesisTime");
                        x.ValidatorCount = section.GetValue<ulong>("ValidatorCount");
                        x.Eth1BlockHash =
                            new Bytes32(section.GetBytesFromPrefixedHex("Eth1BlockHash", () => DefaultEth1BlockHash));
                        x.Eth1Timestamp = section.GetValue<ulong>("Eth1Timestamp");
                        x.UseSystemClock = section.GetValue<bool>("UseSystemClock");
                        x.ValidatorStartIndex = section.GetValue<ulong>("ValidatorStartIndex");
                        x.NumberOfValidators = section.GetValue<ulong>("NumberOfValidators");
                        x.ClockOffset = section.GetValue<long>("ClockOffset");
                    });
                });
            }

            if (configuration.GetValue<ulong>("QuickStart:GenesisTime") > 0)
            {
                services.AddSingleton<IEth1GenesisProvider, QuickStartMockEth1GenesisProvider>();
                services.AddSingleton<IEth1DataProvider, QuickStartMockEth1DataProvider>();
            }
            else if (configuration.GetSection("Eth1Bridge:Nethermind").Exists())
            {
                // TODO: Nethermind Eth1 bridge configuration
                // - create any needed configuration options
                // - implement and register IEth1GenesisProvider
                // - implement and register IEth1DataProvider
                // - implement some kind of collection worker to gather the data and register either as a hosted
                //   service, or modify Eth1BridgeWorker to hook into a collection interface once genesis is complete
            }
            else
            {
                throw new Exception("No Eth1 bridge configuration found.");
            }
        }
    }
}