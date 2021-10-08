//  Copyright (c) 2020 Demerzel Solutions Limited
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
//

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.KeyStore;
using Newtonsoft.Json;

namespace Nethermind.Vault
{
    public class VaultSealingHelper : IVaultSealingHelper
    {
        private readonly IVaultKeyStoreFacade _vaultKeyStoreFacade;
        private readonly IVaultConfig _config;
        private readonly ILogger _logger;
        public VaultSealingHelper(
            IVaultKeyStoreFacade vaultKeyStoreFacade,
            IVaultConfig config,
            ILogger logger)
        {
            _vaultKeyStoreFacade = vaultKeyStoreFacade ?? throw new ArgumentNullException(nameof(vaultKeyStoreFacade));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public async Task Seal()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_config.VaultKeyFile) && string.IsNullOrWhiteSpace(_config.VaultSealUnsealKey))
                {
                    if (_logger.IsInfo) _logger.Info($"Skipping the vault sealing");
                    return;
                }

                var sealResult = await SendSealingRequest("seal");
                if (sealResult.Success)
                {
                    if (_logger.IsInfo) _logger.Info($"The vault sealing was successful.");
                }
                else
                {
                    if (_logger.IsError) _logger.Error($"The vault sealing was failed. {sealResult.Error}");
                }
            }
            catch (Exception ex)
            {
                throw new SecurityException($"Failed to seal vault", ex);
            }
        }

        public async Task Unseal()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_config.VaultKeyFile) && string.IsNullOrWhiteSpace(_config.VaultSealUnsealKey))
                {
                    if (_logger.IsInfo) _logger.Info($"Skipping the vault unsealing");
                    return;
                }

                var unsealResult = await SendSealingRequest("unseal");
                if (unsealResult.Success)
                {
                    if (_logger.IsInfo) _logger.Info($"The vault unsealing was successful.");
                }
                else
                {
                    if (_logger.IsError) _logger.Error($"The vault unsealing was failed. {unsealResult.Error}");
                }
            }
            catch (Exception ex)
            {
                throw new SecurityException($"Failed to unseal vault", ex);
            }
        }

        class SealingUnsealingRequest
        {
            public string key { get; set; }
        }
        public async Task<(bool Success, string Error)> SendSealingRequest(string methodName)
        {
            var request = new SealingUnsealingRequest()
            {
                key = string.IsNullOrWhiteSpace(_config.VaultSealUnsealKey) ? _vaultKeyStoreFacade.GetKey() : _config.VaultSealUnsealKey
            };

            // with a new version of the Provide Nuget package we should remove httpClient call and use the provide.Unseal method
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", _config.Token);
                StringContent content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
                var url = BuildUrl(methodName);
                var response = await httpClient.PostAsync(url.ToString(), content);
                var responseStr = $"Status code: {response.StatusCode}, ResaonPhrase: {response.ReasonPhrase} Content: {response.Content.ReadAsStringAsync().Result}";
                return (response.IsSuccessStatusCode, responseStr);
            }
        }

        private UriBuilder BuildUrl(string methodName)
        {
            int port = -1;
            var host = _config.Host;
            if (host.IndexOf(":") != -1)
            {
                var splittedHost = host.Split(":");
                host = splittedHost[0];
                port = Convert.ToInt32(splittedHost[1]);
            }
            return new UriBuilder(_config.Scheme, host, port, _config.Path + $"/{methodName}");
        }
    }
}
