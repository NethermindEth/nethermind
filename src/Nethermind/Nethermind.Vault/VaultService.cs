using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using provide.Model.Vault;

namespace Nethermind.Vault
{
    public class VaultService : IVaultService
    {
        private readonly ILogger _logger;
        
        private readonly IVaultConfig _vaultConfig;

        private string _host;
        
        private string _path;
        
        private string _scheme;
        
        private string _token;

        private provide.Vault _vaultService;

        public VaultService(IVaultConfig vaultConfig, ILogManager logManager)
        {
            _vaultConfig = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _host = _vaultConfig.Host;
            _path = _vaultConfig.Path;
            _scheme = _vaultConfig.Scheme;
            _token = _vaultConfig.Token;
            _vaultService = new provide.Vault(_host, _path, _scheme, _token);
        }

        public Task ResetToken(string token)
        {
            _token = token;
            InitVaultService();
            return Task.CompletedTask;
        }

        public Task Reset(string scheme, string host, string path, string token)
        {
            _scheme = scheme;
            _host = host;
            _path = path;
            _token = token;
            InitVaultService();
            return Task.CompletedTask;
        }

        public async Task<string[]> ListVaultIds()
        {
            Dictionary<string, object> args = new Dictionary<string, object> {};
            List<provide.Model.Vault.Vault> result = await _vaultService.ListVaults(args);
            return result
                .Where(v => v.Id != null)
                .Select(v => v.Id.ToString()).ToArray();
        }

        public async Task<string> CreateVault(provide.Model.Vault.Vault vault)
        {
            if(_logger.IsDebug) _logger.Debug($"Creating a vault {vault.Name} {vault.Description}");
            provide.Model.Vault.Vault result = await _vaultService.CreateVault(vault);
            return result.Id?.ToString();
        }

        public async Task DeleteVault(string vaultId)
        {
            if(_logger.IsDebug) _logger.Debug($"Deleting vault {vaultId}");
            await _vaultService.DeleteVault(vaultId);
        }
        
        public async Task<Key> CreateKey(string vaultId, Key key)
        {
            if(_logger.IsDebug) _logger.Debug($"Creating a key named {key.Name} in the vault {vaultId}");
            Key vaultKey = await _vaultService.CreateVaultKey(vaultId, key);
            return vaultKey;
        }
        
        public async Task<Key> DeleteKey(string vaultId, string keyId)
        {
            if(_logger.IsDebug) _logger.Debug($"Deleting the key {keyId} in the vault {vaultId}");
            Key vaultKey = await _vaultService.DeleteVaultKey(vaultId, keyId);
            return vaultKey;
        }

        public async Task<string> Sign(string vaultId, string keyId, string message)
        {
            if(_logger.IsDebug) _logger.Debug($"Signing a message with the key {keyId} from the vault {vaultId}");
            SignedMessage result = await _vaultService.SignMessage(vaultId, keyId, message);
            return result.Signature;
        }
        
        public async Task<bool> Verify(string vaultId, string keyId, string message, string signature)
        {
            if(_logger.IsDebug) _logger.Debug($"Verifying a message with the key {keyId} from the vault {vaultId}");
            SignedMessage result = await _vaultService.VerifySignature(vaultId, keyId, message, signature);
            return result.Verified;
        }

        private void InitVaultService()
        {
            if(_logger.IsDebug) _logger.Debug($"Initializing a vault service for {_host} {_path} {_scheme}");
            _vaultService = new provide.Vault(_host, _path, _scheme, _token);
        }
    }
}