//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.Int256;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.DataMarketplace.Consumers.Test.EndToEnd
{
    public class Scenario
    {
        private const string Password = "nethermindtest1234";
        private static string _client;
        private readonly int _pullDataDelay;
        private readonly int _pullDataRetries;
        private readonly int _pullDataFailures;
        private readonly IJsonRpcClientProxy _jsonRpcClient;
        private long _sentQueries;
        private long _receivedResults;
        private readonly IJsonSerializer _serializer;

        public Scenario(string client, string jsonRpcUrl, int pullDataDelay = 10, int pullDataRetries = 10,
            int pullDataFailures = 100)
        {
            _client = client;
            _pullDataDelay = pullDataDelay;
            _pullDataRetries = pullDataRetries;
            _pullDataFailures = pullDataFailures;
            var logsManager = LimboLogs.Instance;
            _serializer = new EthereumJsonSerializer();
            _jsonRpcClient = new JsonRpcClientProxy(new DefaultHttpClient(
                new HttpClient(), _serializer, logsManager, 0), new[] {jsonRpcUrl}, logsManager);
        }

        public async Task RunAsync()
        {
            await VerifyConnectionAsync();
            var account = await CreateAccountAsync();
            await UnlockAccountAsync(account);
            await ChangeAccountAsync(account);
            var providers = await FetchProvidersAsync();
            var dataAssets = await FetchDataAssetsAsync();
            var depositId = await MakeDepositAsync(providers, dataAssets);
            await SendDataRequestAsync(depositId);
            await FetchSessionsAsync();
            await SendQueriesAsync(depositId);
        }

        private async Task VerifyConnectionAsync()
        {
            Separator();
            
            var retries = 0;
            const int maxRetries = 30;
            const int delay = 2000;
            var jsonRpcAvailable = false;
            
            while (!jsonRpcAvailable && retries < maxRetries)
            {
                Log("* Verifying JSON RPC availability *");
                var chainId = await ExecuteAsync<long>("eth_chainId");
                if (chainId > 0)
                {
                    jsonRpcAvailable = true;
                    Log("* JSON RPC is available *");
                    Separator();
                }
                else
                {
                    retries++;
                    Log($"* JSON RPC is not responding, retrying in {delay} ms [{retries}/{maxRetries}] *");
                    await Task.Delay(delay);
                    Separator();
                }
            }
        }

        private async Task<Address> CreateAccountAsync()
        {
            Log("* Creating an account *");
            var account = await ExecuteOrFailAsync<Address>("personal_newAccount", Password);
            Log($"* Created an account: {account} *");
            Separator();

            return account;
        }

        private async Task UnlockAccountAsync(Address account)
        {
            Log($"* Unlocking an account: {account} *");
            var unlockedResult = await ExecuteOrFailAsync<bool>("personal_unlockAccount", account, Password);
            Log($"* Unlocked an account: {account}, {unlockedResult} *");
            Separator();
        }
        
        private async Task ChangeAccountAsync(Address account)
        {
            Log($"* Changing NDM account: {account} *");
            var addressChanged = await ExecuteOrFailAsync<Address>("ndm_changeConsumerAddress", account);
            Log($"* Changed NDM account: {account}, {addressChanged} *");
            Separator();
        }

        private async Task<Address[]> FetchProvidersAsync()
        {
            var retries = 0;
            const int maxRetries = 30;
            const int delay = 2000;

            Address[] addresses = null;
            while ((addresses is null || !addresses.Any()) && retries < maxRetries)
            {
                Log("* Fetching connected providers *");
                addresses = await ExecuteAsync<Address[]>("ndm_getConnectedProviders");
                if (addresses?.Any() is true)
                {
                    Log($"* Connected to providers: {string.Join(", ", addresses.Select(a => a.ToString()))} *");
                }
                else
                {
                    retries++;
                    Log($"* No connected providers found, retrying in {delay} ms [{retries}/{maxRetries}] *");
                    await Task.Delay(delay);
                }
            }

            return addresses;
        }

        private async Task<DataAssetForRpc[]> FetchDataAssetsAsync()
        {
            Separator();
            Log("* Fetching data assets *");
            var dataAssets = await ExecuteOrFailAsync<DataAssetForRpc[]>("ndm_getDiscoveredDataAssets");
            Log($"* Found {dataAssets.Length} data asset(s) *");
            Separator();

            if (!dataAssets.Any())
            {
                Exit();
                return Array.Empty<DataAssetForRpc>();
            }

            foreach (var dataAsset in dataAssets)
            {
                Log(_serializer.Serialize(dataAsset, true));
                Separator();
            }
            
            return dataAssets;
        }

        private async Task<Keccak> MakeDepositAsync(Address[] providers, DataAssetForRpc[] dataAssets)
        {
            var dataAsset = dataAssets.First(d => providers.Contains(d.Provider.Address));
            if (dataAsset is null)
            {
                Log("! Data asset was not found for the available providers addresses !");
                Exit();
                return null;
            }
            
            Log($"* Making a deposit for data asset: {dataAsset.Id} *");
            Separator();
            
            var makeDeposit = new MakeDepositForRpc
            {
                DataAssetId = dataAsset.Id,
                Units = dataAsset.MaxUnits,
                Value = dataAsset.MaxUnits * (UInt256)(dataAsset.UnitPrice ?? 0)
            };
            Log(_serializer.Serialize(makeDeposit, true));
            Separator();
                        
            var depositId = await ExecuteOrFailAsync<Keccak>("ndm_makeDeposit", makeDeposit);
            Log($"* Made a deposit: {depositId} *");
            Separator();

            return depositId;
        }
        
        private async Task SendDataRequestAsync(Keccak depositId)
        {
            Log($"* Sending data request for deposit: {depositId}*");
            var dataRequestStatus = await ExecuteOrFailAsync<string>("ndm_sendDataRequest", depositId);
            Log($"* Received data request status: {dataRequestStatus} *");
            Separator();

            if (!Enum.TryParse<DataRequestResult>(dataRequestStatus, true, out var status))
            {
                Exit();
            }

            if (status != DataRequestResult.DepositVerified)
            {
                Exit();
            }
        }

        private async Task FetchSessionsAsync()
        {
            var retries = 0;
            const int maxRetries = 30;
            const int delay = 2000;

            ConsumerSessionForRpc session = null;
            while (session is null && retries < maxRetries)
            {
                Log("* Fetching active sessions *");
                var sessions = await ExecuteAsync<ConsumerSessionForRpc[]>("ndm_getActiveConsumerSessions");
                session = sessions?.FirstOrDefault();
                if (session is {})
                {
                    session = sessions.First();
                    Log($"* Active session: {session.Id} *");
                }
                else
                {
                    retries++;
                    Log($"* No active sessions found, retrying in {delay} ms [{retries}/{maxRetries}] *");
                    await Task.Delay(delay);
                }
            }
        }

        private async Task SendQueriesAsync(Keccak depositId)
        {
            Log($"Queries will be sent using the client name: {_client}");
            var failedDataPulls = 0;
            
            while (true)
            {
                Log("* Sending a query *");
                var queryArgs = Array.Empty<string>();
                await ExecuteAsync<string>("ndm_enableDataStream", depositId, _client, queryArgs);
                _sentQueries++;
                Log("* Query sent *");

                var dataPulled = false;
                var retries = 0;

                while (!dataPulled && retries < _pullDataRetries)
                {
                    Separator();
                    Log("* Pulling the data *");
                    var data = await ExecuteAsync<string>("ndm_pullData", depositId);
                    if (string.IsNullOrWhiteSpace(data))
                    {
                        Separator();
                        retries++;
                        Log($"* Pulling the data, retrying in {_pullDataDelay} ms [{retries}/{_pullDataRetries}] *");
                        await Task.Delay(_pullDataDelay);
                    }
                    else
                    {
                        dataPulled = true;
                        _receivedResults++;
                        Log(_serializer.Serialize(data, true));
                    }
                }

                Separator();
                if (!dataPulled)
                {
                    failedDataPulls++;
                    Log("! Received an empty data !");
                }

                Separator();
                Log($"- Queries/Results ratio: {_sentQueries}/{_receivedResults} -");
                Separator();

                if (failedDataPulls < _pullDataFailures)
                {
                    continue;
                }
                
                Log($"! Reached max failed data pulls: {failedDataPulls}/{_pullDataFailures} !");
                Exit();
                return;
            }
        }

        private Task<T> ExecuteOrFailAsync<T>(string method, params object[] @params)
            => ExecuteAsync<T>(true, method, @params);

        private Task<T> ExecuteAsync<T>(string method, params object[] @params)
            => ExecuteAsync<T>(false, method, @params);
        
        private async Task<T> ExecuteAsync<T>(bool failForInvalidResult, string method, params object[] @params)
        {
            var result = await _jsonRpcClient.SendAsync<T>(method, @params);
            if (result is null)
            {
                Log($"Did not receive a result for method: '{method}'");
                if (failForInvalidResult)
                {
                    Exit();
                }

                return default;
            }

            if (result.IsValid)
            {
                return result.Result;
            }
            
            Log($"Received an invalid result for method: '{method}'");
            
            if (failForInvalidResult)
            {
                Exit();
            }
            
            return default;
        }

        private static void Exit()
        {
            Log("Exiting E2E scenario... ");
            Environment.Exit(1);
        }
        
        private static void Log(string message) => Console.WriteLine($"{message} [client: {_client}]");
        
        private static void Separator() => Console.WriteLine();
    }
}