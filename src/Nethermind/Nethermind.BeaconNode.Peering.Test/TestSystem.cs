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
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Cryptography;

namespace Nethermind.BeaconNode.Peering.Test
{
    public static class TestSystem
    {
        public static IServiceCollection BuildTestServiceCollection()
        {
            var services = new ServiceCollection();
            
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddJsonFile("Development/appsettings.json")
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Peering:Mothra:LogSignedBeaconBlockJson"] = "false",
                    ["Storage:InMemory:LogBlockJson"] = "false",
                    ["Storage:InMemory:LogBlockStateJson"] = "false"
                })
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            services.AddLogging(configure =>
            {
                configure.SetMinimumLevel(LogLevel.Trace);
                configure.AddConsole(options => { 
                    options.Format = ConsoleLoggerFormat.Systemd;
                    options.DisableColors = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = " HH':'mm':'sszz ";
                });
            });
            
            services.AddBeaconNodePeering(configuration);
            services.AddBeaconNode(configuration);
            services.AddCryptographyService(configuration);
            services.AddBeaconNodeStorage(configuration);
            services.ConfigureBeaconChain(configuration);
            
            return services;
        }

        public static IServiceProvider BuildTestServiceProvider()
        {
            var services = BuildTestServiceCollection();
            var options = new ServiceProviderOptions() { ValidateOnBuild = false };
            return services.BuildServiceProvider(options);
        }
    }
}
