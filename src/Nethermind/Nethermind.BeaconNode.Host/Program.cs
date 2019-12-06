﻿using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cortex.BeaconNode.Storage;
using Cortex.BeaconNode.MockedStart;
using Microsoft.Extensions.Logging.Console;

namespace Cortex.BeaconNode
{
    public class Program
    {
        private const string DefaultProductionYamlConfig = "mainnet";
        private const string DefaultNonProductionYamlConfig = "minimal";
        private const string YamlConfigKey = "config";

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
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
                    configureLogging.AddConsole(consoleLoggerOptions => {
                        //consoleLoggerOptions.Format = ConsoleLoggerFormat.Systemd;
                        consoleLoggerOptions.TimestampFormat = "HH:mm:ss ";
                        consoleLoggerOptions.IncludeScopes = true;
                    });
                })
                .ConfigureAppConfiguration((hostContext, config) =>
                {
                    //var entryAssemblyDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                    //config.SetBasePath(entryAssemblyDirectory);
                    config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);

                    // Base JSON settings
                    config.AddJsonFile("appsettings.json");

                    // Support standard YAML config files
                    var yamlConfig = hostContext.Configuration[YamlConfigKey];
                    if (string.IsNullOrWhiteSpace(yamlConfig))
                    {
                        yamlConfig = hostContext.HostingEnvironment.IsProduction()
                            ? DefaultProductionYamlConfig : DefaultNonProductionYamlConfig;
                        config.AddInMemoryCollection(new Dictionary<string, string> { { YamlConfigKey, yamlConfig } });
                    }
                    config.AddYamlFile($"{yamlConfig}.yaml", true, true);

                    // Override with environment specific JSON files
                    config.AddJsonFile("appsettings." + hostContext.HostingEnvironment.EnvironmentName + ".json", true, true);

                    config.AddCommandLine(args);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddBeaconNode(hostContext.Configuration);
                    services.AddBeaconNodeStorage(hostContext.Configuration);
                    services.AddHostedService<BeaconNodeWorker>();

                    if (hostContext.Configuration.GetValue<ulong>("QuickStart:GenesisTime") > 0)
                    {
                        services.AddQuickStart(hostContext.Configuration);
                    }
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });

        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }
    }
}
