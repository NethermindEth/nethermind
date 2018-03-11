using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;

namespace Nethermind.Runner
{
    public class RunnerApp : IRunnerApp
    {
        private readonly ILogger _logger;

        private string _bootNode = "localhost";
        private int _httpPort = 5000;
        private int _discoveryPort = 10000;
        private string _genesisFile = "genesis.json";

        public RunnerApp(ILogger logger)
        {
            _logger = logger;
        }

        public void Start(string[] args)
        {
            var app = new CommandLineApplication { Name = "Nethermind.Runner" };
            app.HelpOption("-?|-h|--help");

            var bootNode = app.Option("-b|--bootNode <bootNode>", "enode URL of the remote bootstrap node", CommandOptionType.SingleValue);
            var httpPort = app.Option("-p|--httpPort <httpPort>", "JsonRPC http listening port", CommandOptionType.SingleValue);
            var discoveryPort = app.Option("-d|--discoveryPort <discoveryPort>", "discovery UDP listening port", CommandOptionType.SingleValue);
            var genesisFile = app.Option("-g|--genesisFile <genesisFile>", "genesis file name", CommandOptionType.SingleValue);

            app.OnExecute(() => {
                var bootNodeValue = bootNode.HasValue() ? bootNode.Value() : _bootNode;
                var httpPortValue = httpPort.HasValue() ? GetIntValue(httpPort.Value(), "httPort") : _httpPort;
                var discoveryPortValue = discoveryPort.HasValue() ? GetIntValue(discoveryPort.Value(), "discoveryPort") : _discoveryPort;
                var genesisFileValue = genesisFile.HasValue() ? genesisFile.Value() : _genesisFile;

                Console.WriteLine($"Running Nethermind Runner, bootNodeValue: {bootNodeValue}, httpPortValue: {httpPortValue}, discoveryPortValue: {discoveryPortValue}, genesisFile: {genesisFileValue}");

                Run(bootNodeValue, httpPortValue, discoveryPortValue, genesisFileValue);
                return 0;
            });

            app.Execute(args);
        }

        private void Run(string bootNodeValue, int httpPort, int discoveryPort, string genesisFile)
        {
            try
            {
                var webHost = WebHost.CreateDefaultBuilder()
                    .UseStartup<Startup>()
                    .UseUrls($"http://localhost:{httpPort}")
                    .Build();

                var ethereumRunner = webHost.Services.GetService<IEthereumRunner>();
                ethereumRunner.Start(bootNodeValue, discoveryPort, genesisFile);

                var jsonRpcRunner = webHost.Services.GetService<IJsonRpcRunner>();
                Task.Run(() => jsonRpcRunner.Start(webHost));

                Console.WriteLine("Press key to stop");
                Console.ReadLine();
            }
            catch (Exception e)
            {
                _logger.Error("Error while starting Nethermind.Runner", e);
                throw;
            }
        }

        private int GetIntValue(string rawValue, string argName)
        {
            if (int.TryParse(rawValue, out var value))
            {
                return value;
            }

            throw new Exception($"Incorrect argument value, arg: {argName}, value: {rawValue}");
        }
    }
}