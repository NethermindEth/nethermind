using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.Styles;
using Newtonsoft.Json;

namespace Nethermind.Vault
{
    public class VaultManager : IVaultManager
    {
        private readonly IVaultConfig _vaultConfig;

        private readonly ILogger _logger;
        private readonly provide.Vault _initVault;

        public VaultManager(IVaultConfig vaultConfig, ILogManager logManager)
        {
            _vaultConfig = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _initVault = new provide.Vault(_vaultConfig.Host, _vaultConfig.Path, _vaultConfig.Scheme, _vaultConfig.Token);

        }

        public async Task DeleteVault(string vaultId) => await _initVault.DeleteVault(_vaultConfig.Token, vaultId);

        public async Task<string[]> GetVaults()
        {
            List<string> vaultList = new List<string> {};
            Dictionary<string, object> args = new Dictionary<string, object> {};

            var result = await _initVault.ListVaults(_vaultConfig.Token, args);
            dynamic vaults  = JsonConvert.DeserializeObject(result.Item2);
            foreach(var vault in vaults)
            {
                try 
                {
                    string vaultId = Convert.ToString(vault.id);
                    vaultList.Add(vaultId);
                } 
                catch (ArgumentNullException) {}
            }
            return vaultList.ToArray();
        }

        public async Task<string> NewVault(Dictionary<string, object> parameters)
        {
            VaultArgs vaultArgs = parameters["vaultArgs"] as VaultArgs;

            // creates default VaultArgs in case of null input
            if (vaultArgs == null) 
            {
                vaultArgs = new VaultArgs();
                vaultArgs.Name = "name";
                vaultArgs.Description = "description";
            }
            if (!parameters.ContainsKey("vaultArgs")) throw new ArgumentNullException(nameof(parameters));
            
            var result = await _initVault.CreateVault(_vaultConfig.Token, vaultArgs.ToDictionary());
            dynamic vault  = JsonConvert.DeserializeObject(result.Item2);
            string vaultId = Convert.ToString(vault.id);

            return vaultId;
        }
    }
}