// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Cryptography;
using NSubstitute;

namespace Nethermind.BeaconNode.Eth1Bridge.Test
{
    public static class TestSystem
    {
        public static IServiceCollection BuildTestServiceCollection(
            IDictionary<string, string>? overrideConfiguration = null)
        {
            ServiceCollection services = new ServiceCollection();

            services.AddSingleton(Substitute.For<IHostEnvironment>());

            Dictionary<string, string> inMemoryConfiguration = new Dictionary<string, string>
            {
                ["Peering:Mothra:LogSignedBeaconBlockJson"] = "false",
                ["Storage:InMemory:LogBlockJson"] = "false",
                ["Storage:InMemory:LogBlockStateJson"] = "false"
            };
            if (overrideConfiguration != null)
            {
                foreach (KeyValuePair<string, string> kvp in overrideConfiguration)
                {
                    inMemoryConfiguration[kvp.Key] = kvp.Value;
                }
            }

            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddJsonFile("Development/appsettings.json")
                .AddInMemoryCollection(inMemoryConfiguration)
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            services.AddLogging(configure =>
            {
                configure.SetMinimumLevel(LogLevel.Trace);
                configure.AddConsole(options =>
                {
                    options.Format = ConsoleLoggerFormat.Systemd;
                    options.DisableColors = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = " HH':'mm':'sszz ";
                });
            });

            services.ConfigureBeaconChain(configuration);
            services.AddBeaconNode(configuration);
            services.AddCryptographyService(configuration);
            services.AddBeaconNodeEth1Bridge(configuration);

            services.AddBeaconNodeStorage(configuration);

            return services;
        }

        public static IServiceProvider BuildTestServiceProvider()
        {
            IServiceCollection services = BuildTestServiceCollection();
            ServiceProviderOptions options = new ServiceProviderOptions() { ValidateOnBuild = false };
            return services.BuildServiceProvider(options);
        }
    }
}
