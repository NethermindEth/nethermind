using Nethermind.Vault.Config;
using Nethermind.Vault.KeyStore;

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
