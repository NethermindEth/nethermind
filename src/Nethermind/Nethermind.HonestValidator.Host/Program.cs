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
using System.Diagnostics;
using System.IO;
using Essential.LoggerProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.OApiClient;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Cryptography;
using Nethermind.HonestValidator.MockedStart;

namespace Nethermind.HonestValidator.Host
{
    public class Program
    { 
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
                // Default loads host configuration from DOTNET_ and command line,
                // app configuration from appsettings.json, user secrets, environment variables, and command line,
                // configure logging to console, debug, and event source,
                // and, when 'Development', enables scope validation on the dependency injection container.
                .UseWindowsService()
                .ConfigureHostConfiguration(config => {
                    config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                    config.AddJsonFile("hostsettings.json");
                    config.AddCommandLine(args);
                })
                .ConfigureLogging((hostContext, configureLogging) =>
                {
                    if (hostContext.Configuration.GetSection("Logging:Console").Exists())
                    {
                        configureLogging.AddConsole();
                    }
                    if (hostContext.Configuration.GetSection("Logging:Seq").Exists())
                    {
                        configureLogging.AddSeq(hostContext.Configuration.GetSection("Logging:Seq"));
                    }
                    if (hostContext.Configuration.GetSection("Logging:Elasticsearch").Exists())
                    {
                        configureLogging.AddElasticsearch();
                    }
                })
                .ConfigureAppConfiguration((hostContext, config) =>
                {
                    //var entryAssemblyDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                    //config.SetBasePath(entryAssemblyDirectory);
                    config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);

                    // Base JSON settings
                    config.AddJsonFile("appsettings.json");
                    
                    // Override with environment specific JSON files
                    DataDirectory dataDirectory = new DataDirectory(hostContext.Configuration.GetValue<string>(DataDirectory.Key));
                    string settingsPath = Path.Combine(dataDirectory.ResolvedPath, $"appsettings.json");
                    config.AddJsonFile(settingsPath, true, true);

                    config.AddCommandLine(args);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.ConfigureBeaconChain(hostContext.Configuration);
                    services.AddHonestValidator(hostContext.Configuration);
                    services.AddBeaconNodeOapiClient(hostContext.Configuration);
                    services.AddCryptographyService(hostContext.Configuration);
                    
                    if (hostContext.Configuration.GetSection("QuickStart:ValidatorStartIndex").Exists())
                    {
                        services.AddHonestValidatorQuickStart(hostContext.Configuration);
                    }
                });

        public static void Main(string[] args)
        {
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            CreateHostBuilder(args).Build().Run();
        }
    }
}