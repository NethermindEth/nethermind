using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Vault.Config;
using Newtonsoft.Json;

namespace Nethermind.Vault.Test
{
    public class VaultUnsealHelper
    {
        private const string key = "fragile potato army dinner inch enrich decline under scrap soup audit worth trend point cheese sand online parrot faith catch olympic dignity mail crouch";
        class UnsealRequest
        {
            public string key { get; set; }
        }
        public static async Task UnsealVault(IVaultConfig config)
        {
            var request = new UnsealRequest()
            {
                key = key
            };
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("bearer", config.Token);
                StringContent content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
                var url = ConstructUrl(config);
                await httpClient.PostAsync(url, content);
            }
        }

        private static string ConstructUrl(IVaultConfig config)
        {
            return config.Scheme + "://" + config.Host + "/" + config.Path + "/unseal";
        }
    }
}
