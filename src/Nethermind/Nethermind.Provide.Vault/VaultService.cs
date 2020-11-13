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
        private static readonly Dictionary<string, object> EmptyQuery = new Dictionary<string, object>();

        private static readonly Dictionary<string, object> OnlyEthKeys = new Dictionary<string, object>
        {
            {"spec", "secp256k1"}
        };

        private static List<string> AllowedKeyTypes = new List<string>()
        { 
            "asymmetric", "symmetric", "hdwallet"
        };

        private static List<string> AllowedKeySpecs = new List<string>()
        {
            "secp256k1"
        };

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

        public async Task<IEnumerable<Guid>> ListVaultIds()
        {
            List<provide.Model.Vault.Vault> result = await _vaultService.ListVaults(EmptyQuery);
            return result
                .Where(v => v.Id != null)
                .Select(v => v.Id.Value);
        }

        public async Task<provide.Model.Vault.Vault> CreateVault(provide.Model.Vault.Vault vault)
        {
            if (vault.Name == null)
            {
                throw new ArgumentException(
                    $"{nameof(provide.Model.Vault.Vault)} has to have a non-NULL {nameof(vault.Name)}");
            }

            if (vault.Description == null)
            {
                throw new ArgumentException(
                    $"{nameof(provide.Model.Vault.Vault)} has to have a non-NULL {nameof(vault.Description)}");
            }

            if (_logger.IsDebug) _logger.Debug($"Creating a vault {vault.Name} {vault.Description}");
            provide.Model.Vault.Vault result = await _vaultService.CreateVault(vault);
            return result;
        }

        public async Task<provide.Model.Vault.Vault> DeleteVault(Guid vaultId)
        {
            if (_logger.IsDebug) _logger.Debug($"Deleting vault {vaultId}");
            return await _vaultService.DeleteVault(vaultId.ToString());
        }

        public async Task<IEnumerable<Key>> ListKeys(Guid vaultId)
        {
            if (_logger.IsDebug) _logger.Debug("Listing keys");
            return (await _vaultService.ListVaultKeys(vaultId.ToString(), OnlyEthKeys)).Where(k => k.Spec == "secp256k1");
        }

        public async Task<Key> CreateKey(Guid vaultId, Key key)
        {
            if (key.Name == null)
            {
                throw new ArgumentException(
                    $"{nameof(Key)} has to have a non-NULL {nameof(key.Name)}");
            }

            if (key.Description == null)
            {
                throw new ArgumentException(
                    $"{nameof(Key)} has to have a non-NULL {nameof(key.Description)}");
            }

            if (!AllowedKeySpecs.Contains(key.Spec))
            {
                throw new ArgumentException(
                    $"Allowed key specs are: {string.Join(",", AllowedKeySpecs)}.");
            }

            if (!AllowedKeyTypes.Contains(key.Type))
            {
                throw new ArgumentException(
                    $"Allowed key types are: {string.Join(",", AllowedKeyTypes)}.");
            }

            if (_logger.IsDebug) _logger.Debug($"Creating a key named {nameof(key.Name)} in the vault {vaultId}");
            Key vaultKey = await _vaultService.CreateVaultKey(vaultId.ToString(), key);
            return vaultKey;
        }

        public async Task DeleteKey(Guid vaultId, Guid keyId)
        {
            if (_logger.IsDebug) _logger.Debug($"Deleting the key {keyId} in the vault {vaultId}");
            await _vaultService.DeleteVaultKey(vaultId.ToString(), keyId.ToString());
        }

        public async Task<IEnumerable<Secret>> ListSecrets(Guid vaultId)
        {
            if (_logger.IsDebug) _logger.Debug("Listing secrets");
            return await _vaultService.ListVaultSecrets(vaultId.ToString(), EmptyQuery);
        }

        public async Task<Secret> CreateSecret(Guid vaultId, Secret secret)
        {
            if (secret.Name == null)
            {
                throw new ArgumentException(
                    $"{nameof(Secret)} has to have a non-NULL {nameof(secret.Name)}");
            }

            if (secret.Description == null)
            {
                throw new ArgumentException(
                    $"{nameof(Secret)} has to have a non-NULL {nameof(secret.Description)}");
            }

            if (secret.Type == null)
            {
                throw new ArgumentException(
                    $"{nameof(Secret)} has to have a non-NULL {nameof(secret.Type)}");
            }

            if (secret.Value == null)
            {
                throw new ArgumentException(
                    $"{nameof(Secret)} has to have a non-NULL {nameof(secret.Value)}");
            }

            if (_logger.IsDebug) _logger.Debug($"Creating a secret in the vault {vaultId}");
            return await _vaultService.CreateVaultSecret(
                vaultId.ToString(), secret);
        }

        public async Task DeleteSecret(Guid vaultId, Guid secretId)
        {
            if (_logger.IsDebug) _logger.Debug($"Deleting the secret {secretId} in the vault {vaultId}");
            await _vaultService.DeleteVaultSecret(vaultId.ToString(), secretId.ToString());
        }

        public async Task<string> Sign(Guid vaultId, Guid keyId, string message)
        {
            if (_logger.IsDebug) _logger.Debug($"Signing a message with the key {keyId} from the vault {vaultId}");
            SignMessageRequest request = new SignMessageRequest();
            request.Message = message;
            SignMessageResponse response = await _vaultService.SignMessage(
                vaultId.ToString(), keyId.ToString(), request);
            return response.Signature;
        }

        public async Task<bool> Verify(Guid vaultId, Guid keyId, string message, string signature)
        {
            if (_logger.IsDebug) _logger.Debug($"Verifying a message with the key {keyId} from the vault {vaultId}");
            SignatureVerificationRequest request = new SignatureVerificationRequest();
            request.Message = message;
            request.Signature = signature;
            SignatureVerificationResponse response = await _vaultService.VerifySignature(
                vaultId.ToString(), keyId.ToString(), request);
            return response.Verified;
        }

        private void InitVaultService()
        {
            if (_logger.IsDebug) _logger.Debug($"Initializing a vault service for {_host} {_path} {_scheme}");
            _vaultService = new provide.Vault(_host, _path, _scheme, _token);
        }
    }
}
