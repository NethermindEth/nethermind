/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.DataMarketplace.TestRunner.Framework;
using Nethermind.DataMarketplace.TestRunner.JsonRpc;
using Nethermind.DataMarketplace.TestRunner.Tester;
using Nethermind.DataMarketplace.TestRunner.Tester.Scenarios;
using NLog.Extensions.Hosting;

namespace Nethermind.DataMarketplace.TestRunner
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureHostConfiguration(c =>
                {
                    c.SetBasePath(Directory.GetCurrentDirectory());
                    c.AddJsonFile("settings.json", optional: false);
                    c.AddEnvironmentVariables();
                    c.AddCommandLine(args);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddOptions();
                    services.AddLogging();
                    var appOptionsSection = hostContext.Configuration.GetSection("app");
                    var appOptions = new AppOptions();
                    appOptionsSection.Bind(appOptions);
                    services.Configure<AppOptions>(appOptionsSection);
                    services.AddTransient<IProcessBuilder, ProcessBuilder>();
                    services.AddHostedService<NdmTestRunner>();
                    services.AddTransient<INdmTester, NdmTester>();
                    services.AddTransient<DefaultTestScenario>();
                    services.AddTransient<LaunchNodeScenario>();
                    services.AddTransient<CliqueMinersScenario>();
                    services.AddTransient<NdmContext>();
                    services.AddTransient<TestBuilder>();
                })
                .ConfigureLogging((hostContext, configLogging) =>
                {
                    configLogging.ClearProviders();
                    configLogging.SetMinimumLevel(LogLevel.Information);
                })
                .UseNLog()
                .Build();

            try
            {
                await host.RunAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Console.WriteLine("Press RETURN to close.");
                Console.ReadLine();
            }
            
        }
    }
}