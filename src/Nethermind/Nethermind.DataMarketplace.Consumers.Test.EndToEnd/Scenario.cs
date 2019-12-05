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
using Nethermind.Core.Json;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Test.EndToEnd
{
    public class Scenario
    {
        private long _sentQueries;
        private long _receivedResults;
        private readonly IJsonSerializer _serializer;
        private readonly IJsonRpcClientProxy _client;
        private const string Password = "test";

        public Scenario(string jsonRpcUrl)
        {
            var logsManager = LimboLogs.Instance;
            _serializer = new EthereumJsonSerializer();
            _client = new JsonRpcClientProxy(new DefaultHttpClient(new HttpClient(), _serializer, logsManager, 0),
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
            
            Log($"* Changing NDM account: {account} *");
            var addressChanged = await ExecuteAsync<Address>("ndm_changeConsumerAddress", account);
            Log($"* Changed NDM account: {account}, {addressChanged} *");
            Separator();
            
            Address providerAddress = null;
            while (providerAddress is null)
            {
                Log("* Fetching connected providers *");
                var connectedProvidersResult = await ExecuteAsync<Address[]>("ndm_getConnectedProviders");
                if (connectedProvidersResult.Any())
                {
                    providerAddress = connectedProvidersResult.First();
                    Log($"* Connected to provider: {providerAddress} *");
                }
                else
                {
                    const int delay = 2000;
                    Log($"* No connected providers found, retrying in: {delay} ms *");
                    await Task.Delay(delay);
                }
            }

            Separator();
            
            Log("* Fetching data assets *");
            var dataAssets = await ExecuteAsync<DataAssetForRpc[]>("ndm_getDiscoveredDataAssets");
            Log($"* Found {dataAssets.Length} data asset(s) *");
            Separator();
            
            foreach (var dataAsset in dataAssets)
            {
                Log(_serializer.Serialize(dataAsset, true));
                Separator();
            }

            var firstDataAsset = dataAssets.First();
            Log($"* Making a deposit for data asset: {firstDataAsset.Id} *");
            Separator();
            
            var makeDeposit = new MakeDepositForRpc
            {
                DataAssetId = firstDataAsset.Id,
                Units = firstDataAsset.MaxUnits,
                Value = firstDataAsset.MaxUnits * new UInt256(firstDataAsset.UnitPrice)
            };
            Log(_serializer.Serialize(makeDeposit, true));
            Separator();
            
            var depositId = await ExecuteAsync<Keccak>("ndm_makeDeposit", makeDeposit);
            Log($"* Made a deposit: {depositId} *");
            Separator();

            Log($"* Sending data request for deposit: {depositId}*");
            var dataRequestStatus = await ExecuteAsync<string>("ndm_sendDataRequest", depositId);
            Log($"* Received data request status: {dataRequestStatus} *");
            Separator();
            
            ConsumerSessionForRpc session = null;
            while (session is null)
            {
                Log("* Fetching active sessions *");
                var sessions = await ExecuteAsync<ConsumerSessionForRpc[]>("ndm_getActiveConsumerSessions");
                if (sessions.Any())
                {
                    session = sessions.First();
                    Log($"* Active session: {session.Id} *");
                }
                else
                {
                    const int delay = 2000;
                    Log($"* No active sessions found, retrying in: {delay} ms *");
                    await Task.Delay(delay);
                }
            }

            var client = Environment.GetEnvironmentVariable("HOSTNAME") ?? "ndm";
            Log($"Queries will be sent using the client name: {client}");
            
            while (true)
            {
                Log("* Sending a query *");
                var queryArgs = Array.Empty<string>();
                await ExecuteAsync<string>("ndm_enableDataStream", depositId, client, queryArgs);
                _sentQueries++;
                Log("* Query sent *");
                Separator();

                await Task.Delay(10);
                
                Log("* Pulling the data *");
                var data = await ExecuteAsync<string>("ndm_pullData", depositId);
                Log("* Data pulled *");
                Separator();
                
                if (string.IsNullOrWhiteSpace(data))
                {
                    Log("! Received an empty data !");
                }
                else
                {
                    _receivedResults++;
                    Log(_serializer.Serialize(data, true));
                }
                
                Separator();
                
                Log($"- Queries/Results ratio: {_sentQueries}/{_receivedResults} -");
                
                Separator();
            }
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