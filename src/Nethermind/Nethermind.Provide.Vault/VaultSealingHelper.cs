using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Vault.Config;
using Nethermind.Vault.KeyStore;
using Newtonsoft.Json;

namespace Nethermind.Vault
{
    public class VaultSealingHelper : IVaultSealingHelper
    {
        private readonly IVaultKeyStoreFacade _vaultKeyStoreFacade;
        private readonly IVaultConfig _config;
        public VaultSealingHelper(
            IVaultKeyStoreFacade vaultKeyStoreFacade,
            IVaultConfig config)
        {
            _vaultKeyStoreFacade = vaultKeyStoreFacade ?? throw new ArgumentNullException(nameof(vaultKeyStoreFacade));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }
        public void Seal()
        {
            var sealTask = SealingUnsealingMethod("seal");
            sealTask.Wait();
        }

        public void Unseal()
        {
            var unsealTask = SealingUnsealingMethod("unseal");
            unsealTask.Wait();
        }

        class SealingUnsealingRequest
        {
            public string key { get; set; }
        }
        public async Task SealingUnsealingMethod(string methodName)
        {
            var request = new SealingUnsealingRequest()
            {
                key = _vaultKeyStoreFacade.GetKey()
            };

            // with a new version of the Provide Nuget package we should remove httpClient call and use the provide.Unseal method
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", _config.Token);
                StringContent content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
                var url = ConstructUrl(methodName);
                await httpClient.PostAsync(url, content);
            }
        }

        private string ConstructUrl(string methodName)
        {
            return _config.Scheme + "://" + _config.Host + "/" + _config.Path + $"/{methodName}";
        }
    }
}
