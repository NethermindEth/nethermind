using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.BeamSyncWallet.Modules.Data;
using Nethermind.BeamSyncWallet.Modules.Main;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.BeamSyncWallet
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Application.Init();

            var mainModule = new MainModule();
            var urls = new[] {"http://178.62.30.91:8765/"};

            var ethJsonRpcClientProxy = new EthJsonRpcClientProxy(new JsonRpcClientProxy(new DefaultHttpClient(
                new HttpClient(), new EthereumJsonSerializer(), LimboLogs.Instance), urls, LimboLogs.Instance));
            
            mainModule.AddressSelected += async (_, address) =>
            {
                var dataModule = new DataModule(ethJsonRpcClientProxy, address);
                var dataModuleWindow = await dataModule.InitAsync();
                Application.Top.Add(dataModuleWindow);
                Application.Run(dataModuleWindow);
            };
            Application.Top.Add(await mainModule.InitAsync());
            Application.Run();
        }
    }
}
