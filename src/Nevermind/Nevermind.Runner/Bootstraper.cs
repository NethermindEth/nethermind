using Nevermind.Core;
using Nevermind.Json;
using Nevermind.JsonRpc;
using Nevermind.JsonRpc.Module;
using Unity;

namespace Nevermind.Runner
{
    public class Bootstraper
    {
        public IUnityContainer Container { get; set; }

        public Bootstraper()
        {
            Container = new UnityContainer();
            ConfigureContainer();
        }

        private void ConfigureContainer()
        {
            Container.RegisterType<ILogger, ConsoleLogger>();
            Container.RegisterType<IConfigurationProvider, ConfigurationProvider>();
            Container.RegisterType<IJsonSerializer, JsonSerializer>();
            Container.RegisterType<INetModule, NetModule>();
            Container.RegisterType<IWeb3Module, Web3Module>();
            Container.RegisterType<IEthModule, EthModule>();
            Container.RegisterType<IJsonRpcService, JsonRpcService>();

            Container.RegisterType<IJsonRpcRunner, JsonRpcRunner>();
            Container.RegisterType<IEthereumRunner, EthereumRunner>();
        }
    }
}