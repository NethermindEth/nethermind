using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Clients;
using Nethermind.BeamWallet.Modules.Addresses;
using Nethermind.BeamWallet.Modules.Data;
using Nethermind.BeamWallet.Modules.Init;
using Nethermind.BeamWallet.Modules.Transfer;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.BeamWallet
{
    class Program
    {
        private const string DefaultUrl = "http://localhost:8545";

        static async Task Main(string[] args)
        {

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Console.WriteLine($"There was an error.{Environment.NewLine}{e.ExceptionObject}");
            };
            Application.Init();
            var initModule = new InitModule();
            initModule.OptionSelected += async (_, optionInfo) =>
            {
                var urls = new[] {DefaultUrl};
                var httpClient = new HttpClient();
                var jsonRpcClientProxy = new JsonRpcClientProxy(new DefaultHttpClient(httpClient,
                    new EthereumJsonSerializer(), LimboLogs.Instance, int.MaxValue), urls, LimboLogs.Instance);
                var jsonRpcWalletClientProxy = new JsonRpcWalletClientProxy(jsonRpcClientProxy);
                var ethJsonRpcClientProxy = new EthJsonRpcClientProxy(jsonRpcClientProxy);
                var addressesModule = new AddressesModule(optionInfo.Item1, jsonRpcWalletClientProxy, optionInfo.Item2);

                addressesModule.AddressesSelected += async (_, addressesEvent) =>
                {
                    urls = new[] {addressesEvent.NodeAddress};

                    AddAuthorizationHeader(httpClient, addressesEvent.NodeAddress);

                    var dataModule = new DataModule(ethJsonRpcClientProxy, addressesEvent.AccountAddress);
                    dataModule.TransferClicked += async (_, transferEvent) =>
                    {
                        var transferModule = new TransferModule(ethJsonRpcClientProxy, jsonRpcWalletClientProxy,
                            transferEvent.Address, transferEvent.Balance);
                        var transferWindow = await transferModule.InitAsync();
                        Application.Top.Add(transferWindow);
                        Application.Run(transferWindow);
                    };
                    var dataWindow = await dataModule.InitAsync();
                    Application.Top.Add(dataWindow);
                    Application.Run(dataWindow);
                };
                var addressesWindow = await addressesModule.InitAsync();
                Application.Top.Add(addressesWindow);
                Application.Run(addressesWindow);
            };
            
            var initWindow = await initModule.InitAsync();
            Application.Top.Add(initWindow);
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
