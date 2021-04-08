using System;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.DataMarketplace.Providers.Test.EndToEnd
{
    public class Scenario
    {
        private readonly ITimestamper _timestamper = new Timestamper();
        private readonly IJsonRpcClientProxy _client;
        private const string Password = "test";

        public Scenario(string jsonRpcUrl)
        {
            var logsManager = LimboLogs.Instance;
            var serializer = new EthereumJsonSerializer();
            _client = new JsonRpcClientProxy(new DefaultHttpClient(new HttpClient(), serializer, logsManager, 0),
                new[] {jsonRpcUrl}, logsManager);
        }

        public async Task RunAsync()
        {
            Separator();

            var jsonRpcAvailable = false;
            while (!jsonRpcAvailable)
            {
                Log("* Verifying JSON RPC availability *");
                var chainId = await ExecuteAsync<long>("eth_chainId");
                if (chainId > 0)
                {
                    jsonRpcAvailable = true;
                    Log($"* JSON RPC is available *");
                    Separator();
                }
                else
                {
                    const int delay = 2000;
                    Log($"* JSON RPC is not responding, retrying in: {delay} ms *");
                    await Task.Delay(delay);
                    Separator();
                }
            }

            Log("* Creating an account *");
            var account = await ExecuteAsync<Address>("personal_newAccount", Password);
            Log($"* Created an account: {account} *");
            Separator();

            Log($"* Unlocking an account: {account} *");
            var unlockedResult = await ExecuteAsync<bool>("personal_unlockAccount", account, Password);
            Log($"* Unlocked an account: {account}, {unlockedResult} *");
            Separator();

            Log($"* Changing NDM provider account: {account} *");
            var addressChanged = await ExecuteAsync<Address>("ndm_changeProviderAddress", account);
            Log($"* Changed NDM provider account: {account}, {addressChanged} *");
            Separator();

            Log($"* Changing NDM provider cold wallet account: {account} *");
            var coldWalletChanged = await ExecuteAsync<Address>("ndm_changeProviderColdWalletAddress", account);
            Log($"* Changed NDM provider cold wallet account: {account}, {coldWalletChanged} *");
            Separator();

            Log("* Adding the the data asset *");
            var addDataAsset = new DataAssetForRpc
            {
                Name = "Sample Web API",
                Description = "Sample Web API test",
                UnitPrice = 1.GWei(),
                UnitType = DataAssetUnitType.Unit.ToString(),
                QueryType = QueryType.Query.ToString(),
                Plugin = "sample-web-api",
                MinUnits = 1,
                MaxUnits = 1000000,
                Rules = new DataAssetRulesForRpc
                {
                    Expiry = new DataAssetRuleForRpc
                    {
                        Value = 1000000
                    }
                }
            };

            var dataAssetId = await ExecuteAsync<Keccak>("ndm_addDataAsset", addDataAsset);
            Log($"* Added the data asset: {dataAssetId} *");
            Separator();

            var state = DataAssetState.Published.ToString();
            Log($"* Changing the data asset state to: {state}*");
            var changedState = await ExecuteAsync<bool>("ndm_changeDataAssetState", dataAssetId, state);
            Log($"* Changed the data asset state to: {state}, {changedState}*");
            Separator();

            Log($"* NDM provider is now ready to accept the data requests *");
            Separator();
        }

        private async Task<T> ExecuteAsync<T>(string method, params object[] @params)
        {
            var result = await _client.SendAsync<T>(method, @params);
            if (result is null)
            {
                Log($"Did not receive a result for method: '{method}'");
                return default;
            }

            if (result.IsValid)
            {
                return result.Result;
            }

            Log($"Received an invalid result for method: '{method}'");

            return default;
        }

        private static void Log(string message) => Console.WriteLine(message);
        private static void Separator() => Console.WriteLine();
    }
}