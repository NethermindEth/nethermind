// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
