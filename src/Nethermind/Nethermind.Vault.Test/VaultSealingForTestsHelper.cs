using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Vault.Config;
using Nethermind.Vault.KeyStore;
using Newtonsoft.Json;

namespace Nethermind.Vault.Test
{
    public class VaultSealingForTestsHelper
    {
        public static void Unseal(IVaultConfig config)
        {
            var vaultKeyStoreFacade = new VaultKeyStoreFacade();
            var vaultSealingHelper = new VaultSealingHelper(vaultKeyStoreFacade, config);
            vaultSealingHelper.Unseal();
        }
    }
}
