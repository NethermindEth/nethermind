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
using Nethermind.BeaconNode.OApiClient;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Cryptography;

namespace Nethermind.HonestValidator.Test
{
    public static class TestSystem
    {
        public static IServiceCollection BuildTestServiceCollection()
        {
            var services = new ServiceCollection();
            
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddJsonFile("Development/appsettings.json")
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            services.AddLogging(configure =>
            {
                configure.SetMinimumLevel(LogLevel.Trace);
                configure.AddConsole();
            });
            
            services.ConfigureBeaconChain(configuration);
            services.AddHonestValidator(configuration);
            services.AddBeaconNodeOapiClient(configuration);
            services.AddCryptographyService(configuration);
            
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
