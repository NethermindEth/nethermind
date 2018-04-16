using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Runner.Runners;

namespace Nethermind.Runner
{
    public abstract class BaseRunnerApp
    {
        protected readonly ILogger Logger;
        protected readonly IPrivateKeyProvider PrivateKeyProvider;
        private IJsonRpcRunner _jsonRpcRunner;
        private IEthereumRunner _ethereumRunner;
        private IDiscoveryRunner _discoveryRunner;

        protected BaseRunnerApp(ILogger logger, IPrivateKeyProvider privateKeyProvider)
        {
            Logger = logger;
            PrivateKeyProvider = privateKeyProvider;
        }

        protected void StartRunners(InitParams initParams)
        {
            try
            {
                //Configuring app DI
                var configProvider = new ConfigurationProvider();
                Bootstrap.ConfigureContainer(configProvider, PrivateKeyProvider, Logger, initParams);

                //It needs to run first to finalize objects registration in the container
                _jsonRpcRunner = new JsonRpcRunner(configProvider, Logger);
                _jsonRpcRunner.Start(initParams);

                _ethereumRunner = Bootstrap.ServiceProvider.GetService<IEthereumRunner>();
                _ethereumRunner.Start(initParams);

                _discoveryRunner = Bootstrap.ServiceProvider.GetService<IDiscoveryRunner>();
                _discoveryRunner.Start(initParams);
            }
            catch (Exception e)
            {
                Logger.Error("Error while starting Nethermind.Runner", e);
                throw;
            }
        }

        protected abstract (CommandLineApplication, Func<InitParams>) BuildCommandLineApp();

        public void Run(string[] args)
        {
            (var app, var buildInitParams) = BuildCommandLineApp();
            ManualResetEvent appClosed = new ManualResetEvent(false);
            app.OnExecute(() =>
            {
                var initParams = buildInitParams();
                Logger.Log($"Running Hive Nethermind Runner, parameters: {initParams}");

                StartRunners(initParams);

                Console.WriteLine("Enter 'e' to exit");
                while (true)
                {
                    ConsoleKeyInfo keyInfo = Console.ReadKey();
                    if (keyInfo.KeyChar == 'e')
                    {
                        break;
                    }
                }

                Console.WriteLine("Closing, please wait until all functions are stopped properly...");
                StopAsync().Wait();
                Console.WriteLine("All done, goodbye!");
                appClosed.Set();

                return 0;
            });

            app.Execute(args);
            appClosed.WaitOne();
        }

        protected async Task StopAsync()
        {
            await _jsonRpcRunner.StopAsync();
            await _discoveryRunner.StopAsync();
            await _ethereumRunner.StopAsync();
        }

        protected int GetIntValue(string rawValue, string argName)
        {
            if (int.TryParse(rawValue, out var value))
            {
                return value;
            }

            throw new Exception($"Incorrect argument value, arg: {argName}, value: {rawValue}");
        }

        protected BigInteger GetBigIntValue(string rawValue, string argName)
        {
            if (BigInteger.TryParse(rawValue, out var value))
            {
                return value;
            }

            throw new Exception($"Incorrect argument value, arg: {argName}, value: {rawValue}");
        }
    }
}