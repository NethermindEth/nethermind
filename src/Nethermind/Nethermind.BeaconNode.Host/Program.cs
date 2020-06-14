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
using System.Diagnostics;
using System.IO;
using Essential.LoggerProvider;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using Nethermind.BeaconNode.Eth1Bridge;
using Nethermind.BeaconNode.MockedStart;
using Nethermind.BeaconNode.Peering;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Cryptography;
using Nethermind.HonestValidator;
using Nethermind.HonestValidator.MockedStart;

namespace Nethermind.BeaconNode.Host
{
    public class Program
    {
        private const string _yamlConfigKey = "config";

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
                    // this causes MASSIVE slowdown on BeaconNode start - please review
                    // (try with 10000 validators with and without logging)
                    
                    // if (hostContext.Configuration.GetSection("Logging:Seq").Exists())
                    // {
                    //     configureLogging.AddSeq(hostContext.Configuration.GetSection("Logging:Seq"));
                    // }
                    // if (hostContext.Configuration.GetSection("Logging:Elasticsearch").Exists())
                    // {
                    //     configureLogging.AddElasticsearch();
                    // }
                })
                .ConfigureAppConfiguration((hostContext, config) =>
                {
                    //var entryAssemblyDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                    //config.SetBasePath(entryAssemblyDirectory);
                    config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);

                    // Base JSON settings
                    config.AddJsonFile("appsettings.json");

                    // Other settings are based on data directory; can be relative, or start with a special folder token,
                    // e.g. "{CommonApplicationData}/Nethermind/BeaconHost/Production"
                    DataDirectory dataDirectory = new DataDirectory(hostContext.Configuration.GetValue<string>(DataDirectory.Key));
                        
                    // Support standard YAML config files, if specified
                    string yamlConfig = hostContext.Configuration[_yamlConfigKey];
                    if (!string.IsNullOrWhiteSpace(yamlConfig))
                    {
                        string yamlPath = Path.Combine(dataDirectory.ResolvedPath, $"{yamlConfig}.yaml");
                        config.AddYamlFile(yamlPath, true, true);
                    }
                    
                    // Override with environment specific JSON files
                    string settingsPath = Path.Combine(dataDirectory.ResolvedPath, $"appsettings.json");
                    config.AddJsonFile(settingsPath, true, true);

                    config.AddCommandLine(args);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.ConfigureBeaconChain(hostContext.Configuration);
                    services.AddBeaconNode(hostContext.Configuration);
                    services.AddBeaconNodeStorage(hostContext.Configuration);
                    services.AddBeaconNodePeering(hostContext.Configuration);
                    services.AddBeaconNodeEth1Bridge(hostContext.Configuration);
                    services.AddCryptographyService(hostContext.Configuration);

                    if (hostContext.Configuration.GetValue<ulong>("QuickStart:GenesisTime") > 0)
                    {
                        services.AddBeaconNodeQuickStart(hostContext.Configuration);
                    }
                    
                    // TODO: Add non-quickstart validator check
                    if (hostContext.Configuration.GetSection("QuickStart:ValidatorStartIndex").Exists())
                    {
                        services.AddHonestValidator(hostContext.Configuration);
                        services.AddHonestValidatorQuickStart(hostContext.Configuration);
                    }
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });

        public static void Main(string[] args)
        {
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            CreateHostBuilder(args).Build().Run();
        }
    }
}
