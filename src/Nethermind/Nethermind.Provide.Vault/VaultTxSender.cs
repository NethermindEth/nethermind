// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.TxPool;
using Nethermind.Vault.Config;
using Newtonsoft.Json;
using provide;
using ProvideTx = provide.Model.NChain.Transaction;

namespace Nethermind.Vault
{
    public class VaultTxSender : ITxSender
    {
        public class CreateAccountRequest
        {
            public string network_id { get; set; }
        }

        private static Dictionary<int, Guid> _networkIdMapping = new Dictionary<int, Guid>
        {
            // Bitcoin mainnet: 248fa53c-3df4-41af-ab6b-b78f94d6c25c 	 mainnet 
            {1, new Guid("deca2436-21ba-4ff5-b225-ad1b0b2f5c59")}, // Ethereum mainnet
            {3, new Guid("66d44f30-9092-4182-a3c4-bc02736d6ae5")}, // Ethereum Ropsten testnet
            {4, new Guid("07102258-5e49-480e-86af-6d0c3260827d")}, // Ethereum Rinkeby testnet
            {5, new Guid("1b16996e-3595-4985-816c-043345d22f8c")}, // Ethereum Görli Testnet
            {42, new Guid("8d31bf48-df6b-4a71-9d7c-3cb291111e27")} // Ethereum Kovan testnet 
        };

        private Guid? _networkId;
        private Guid? _accountId;
        private readonly ITxSigner _txSigner;
        private readonly IVaultConfig _vaultConfig;

        private NChain _provide;

        public VaultTxSender(ITxSigner txSigner, IVaultConfig vaultConfig, int chainId)
        {
            _txSigner = txSigner;
            _vaultConfig = vaultConfig;

            _provide = new NChain(
                vaultConfig.NChainHost,
                vaultConfig.NChainPath,
                vaultConfig.NChainScheme,
                vaultConfig.NChainToken);

            EnsureNetwork(chainId);
        }

        private void EnsureNetwork(int chainId)
        {
            if (_networkId == null)
            {
                if (string.IsNullOrWhiteSpace(_vaultConfig.NChainNetworkId))
                {
                    _networkId = _networkIdMapping[chainId];
                }
                else
                {
                    _networkId = new Guid(_vaultConfig.NChainNetworkId);
                }
            }
        }

        private async Task EnsureAccount()
        {
            if (_accountId == null)
            {
                if (string.IsNullOrWhiteSpace(_vaultConfig.NChainAccountId))
                {
                    var createRequest = new CreateAccountRequest()
                    {
                        network_id = _networkId!.Value.ToString()
                    };
                    var result = await SendPostNChainRequest("accounts", createRequest);
                    var json = await result.Content.ReadAsStringAsync();
                    var account = JsonConvert.DeserializeObject<provide.Model.NChain.Account>(json);
                    _accountId = account.Id;
                }
                else
                {
                    _accountId = new Guid(_vaultConfig.NChainAccountId);
                }
            }
        }

        public async ValueTask<(Keccak, AcceptTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            await EnsureAccount();
            ProvideTx provideTx = new ProvideTx();
            provideTx.Data = tx.Data?.ToHexString(true);
            provideTx.Description = "From Nethermind with love";
            provideTx.Hash = tx.Hash?.ToString();
            provideTx.AccountId = _accountId;
            provideTx.NetworkId = _networkId;
            provideTx.To = tx.To?.ToString();
            provideTx.Value = (BigInteger)tx.Value;
            provideTx.Params = new string[] { "subsidize", "true" };
            // this should happen after we set the GasPrice
            await _txSigner.Seal(tx, TxHandlingOptions.None);
            ProvideTx createdTx = await _provide.CreateTransaction(provideTx);
            return (createdTx?.Hash == null ? Keccak.Zero : new Keccak(createdTx.Hash), null);
        }

        public async Task<HttpResponseMessage> SendPostNChainRequest(string methodName, CreateAccountRequest request)
        {
            // with a new version of the Provide Nuget package we should remove httpClient call
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", _vaultConfig.NChainToken);
                StringContent content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
                var url = BuildUrl(methodName);
                return await httpClient.PostAsync(url.ToString(), content);
            }
        }

        private UriBuilder BuildUrl(string methodName)
        {
            int port = -1;
            var host = _vaultConfig.NChainHost;
            if (host.IndexOf(":") != -1)
            {
                var splittedHost = host.Split(":");
                host = splittedHost[0];
                port = Convert.ToInt32(splittedHost[1]);
            }
            return new UriBuilder(_vaultConfig.NChainScheme, host, port, _vaultConfig.NChainPath + $"/{methodName}");
        }
    }
}
