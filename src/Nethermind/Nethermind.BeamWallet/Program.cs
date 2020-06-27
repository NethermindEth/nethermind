using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Clients;
using Nethermind.BeamWallet.Modules.Data;
using Nethermind.BeamWallet.Modules.Main;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.BeamWallet
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Application.Init();

            var mainModule = new MainModule();
            var urls = new[] {"http://localhost:8545/"};

            var jsonRpcClientProxy = new JsonRpcClientProxy(new DefaultHttpClient(new HttpClient(),
                new EthereumJsonSerializer(), LimboLogs.Instance), urls, LimboLogs.Instance);

            var jsonRpcWalletClientProxy = new JsonRpcWalletClientProxy(jsonRpcClientProxy);
            var ethJsonRpcClientProxy = new EthJsonRpcClientProxy(jsonRpcClientProxy);
            
            mainModule.AddressSelected += async (_, address) =>
            {
                var dataModule = new DataModule(ethJsonRpcClientProxy, jsonRpcWalletClientProxy, address);
                var dataModuleWindow = await dataModule.InitAsync();
                Application.Top.Add(dataModuleWindow);
                Application.Run(dataModuleWindow);
            };
            Application.Top.Add(await mainModule.InitAsync());
            Application.Run();
        }
    }
}
