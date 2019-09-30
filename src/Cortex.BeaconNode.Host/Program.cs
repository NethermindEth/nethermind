using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cortex.BeaconNode
{
    public class Program
    {
        private const string DefaultYamlConfig = "mainnet";
        private const string YamlConfigKey = "config";

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                // Default loads host configuration from DOTNET_ and command line,
                // app configuration from appsettings.json, user secrets, environment variables, and command line,
                // configure logging to console, debug, and event source,
                // and, when 'Development', enables scope validation on the dependency injection container.
                .UseWindowsService()
                .ConfigureAppConfiguration((hostContext, config) =>
                {
                    var yamlConfig = hostContext.Configuration[YamlConfigKey];
                    if (string.IsNullOrWhiteSpace(yamlConfig))
                    {
                        yamlConfig = DefaultYamlConfig;
                        config.AddInMemoryCollection(new Dictionary<string, string> { { YamlConfigKey, yamlConfig } });
                    }
                    config.AddYamlFile($"{yamlConfig}.yaml");
                    config.AddCommandLine(args);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddBeaconNode();
                    services.AddHostedService<Worker>();
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
