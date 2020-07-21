using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Clients;
using Nethermind.BeamWallet.Modules.Addresses;
using Nethermind.BeamWallet.Modules.Data;
using Nethermind.BeamWallet.Modules.Transfer;
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
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Console.WriteLine($"There was an error.{Environment.NewLine}{e.ExceptionObject}");
            };
            Application.Init();
            var addressesModule = new AddressesModule();

            addressesModule.AddressesSelected += async (_, data) =>
            {
                var urls = new[] {data.nodeAddress};
                var httpClient = new HttpClient();

                AddAuthorizationHeader(httpClient, data.nodeAddress);
                
                var jsonRpcClientProxy = new JsonRpcClientProxy(new DefaultHttpClient(httpClient,
                    new EthereumJsonSerializer(), LimboLogs.Instance, int.MaxValue), urls, LimboLogs.Instance);

                var jsonRpcWalletClientProxy = new JsonRpcWalletClientProxy(jsonRpcClientProxy);
                var ethJsonRpcClientProxy = new EthJsonRpcClientProxy(jsonRpcClientProxy);
                
                var dataModule = new DataModule(ethJsonRpcClientProxy, data.address);
                dataModule.TransferClicked += async (_, e) =>
                {
                    var transferModule = new TransferModule(ethJsonRpcClientProxy, jsonRpcWalletClientProxy,
                        e.Address, e.Balance);
                    var transferWindow = await transferModule.InitAsync();
                    Application.Top.Add(transferWindow);
                    Application.Run(transferWindow);
                };
                var dataWindow = await dataModule.InitAsync();
                Application.Top.Add(dataWindow);
                Application.Run(dataWindow);
            };
            Application.Top.Add(await addressesModule.InitAsync());
            Application.Run();
        }
        
        private static void AddAuthorizationHeader(HttpClient httpClient, string url)
        {
            if (!url.Contains("@"))
            {
                return;
            }

            var urlData = url.Split("://");
            var data = urlData[1].Split("@")[0];

            var encodedData = Base64Encode(data);

            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encodedData);
        }
        
        private static string Base64Encode(string plainText)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
    }
}
