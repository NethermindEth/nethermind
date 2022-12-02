// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
