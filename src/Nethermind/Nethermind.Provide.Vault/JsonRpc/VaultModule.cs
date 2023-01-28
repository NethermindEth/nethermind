// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Logging;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using provide.Model.Vault;

namespace Nethermind.Vault.JsonRpc
{
    public class VaultModule : IVaultModule
    {
        private readonly ILogger _logger;

        private readonly IVaultService _vaultService;

        public VaultModule(IVaultService vaultService, ILogManager logManager)
        {
            _vaultService = vaultService ?? throw new ArgumentNullException(nameof(vaultService));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<ResultWrapper<Key>> vault_createKey(string vaultId, Key key)
        {
            Key result = await _vaultService.CreateKey(Guid.Parse(vaultId), key);
            return ResultWrapper<Key>.Success(result);
        }

        public async Task<ResultWrapper<Secret>> vault_createSecret(string vaultId, Secret secret)
        {
            try
            {
                Secret result = await _vaultService.CreateSecret(Guid.Parse(vaultId), secret);
                return ResultWrapper<Secret>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<Secret>.Fail(e);
            }
        }

        public async Task<ResultWrapper<provide.Model.Vault.Vault>> vault_createVault(provide.Model.Vault.Vault vault)
        {
            try
            {
                provide.Model.Vault.Vault vaultId = await _vaultService.CreateVault(vault);
                return ResultWrapper<provide.Model.Vault.Vault>.Success(vaultId);
            }
            catch (Exception e)
            {
                return ResultWrapper<provide.Model.Vault.Vault>.Fail(e);
            }
        }

        public async Task<ResultWrapper<bool>> vault_deleteVault(string vaultId)
        {
            try
            {
                await _vaultService.DeleteVault(Guid.Parse(vaultId));
                return ResultWrapper<bool>.Success(true);
            }
            catch (Exception e)
            {
                return ResultWrapper<bool>.Fail(e);
            }
        }

        public async Task<ResultWrapper<bool>> vault_deleteKey(string vaultId, string keyId)
        {
            try
            {
                await _vaultService.DeleteKey(Guid.Parse(vaultId), Guid.Parse(keyId));
                return ResultWrapper<bool>.Success(true);
            }
            catch (Exception e)
            {
                return ResultWrapper<bool>.Fail(e);
            }
        }

        public async Task<ResultWrapper<bool>> vault_deleteSecret(string vaultId, string secretId)
        {
            try
            {
                await _vaultService.DeleteSecret(Guid.Parse(vaultId), Guid.Parse(secretId));

                return ResultWrapper<bool>.Success(true);
            }
            catch (Exception e)
            {
                return ResultWrapper<bool>.Fail(e);
            }
        }

        public async Task<ResultWrapper<string[]>> vault_listVaults()
        {
            try
            {
                var result = await _vaultService.ListVaultIds();

                return ResultWrapper<string[]>.Success(result.Select(id => id.ToString()).ToArray());
            }
            catch (Exception e)
            {
                return ResultWrapper<string[]>.Fail(e);
            }
        }

        public async Task<ResultWrapper<Key[]>> vault_listKeys(string vaultId)
        {
            try
            {
                var keys = await _vaultService.ListKeys(Guid.Parse(vaultId));
                return ResultWrapper<Key[]>.Success(keys.ToArray());
            }
            catch (Exception e)
            {
                return ResultWrapper<Key[]>.Fail(e);
            }
        }

        public async Task<ResultWrapper<Secret[]>> vault_listSecrets(string vaultId)
        {
            try
            {
                var secrets = await _vaultService.ListSecrets(Guid.Parse(vaultId));
                return ResultWrapper<Secret[]>.Success(secrets.ToArray());
            }
            catch (Exception e)
            {
                return ResultWrapper<Secret[]>.Fail(e);
            }
        }

        public async Task<ResultWrapper<string>> vault_signMessage(string vaultId, string keyId, string message)
        {
            try
            {
                ValidateMessage(message);
                string result = await _vaultService.Sign(Guid.Parse(vaultId), Guid.Parse(keyId), message);
                return ResultWrapper<string>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<string>.Fail(e);
            }
        }

        public async Task<ResultWrapper<bool>> vault_verifySignature(
            string vaultId, string keyId, string message, string signature)
        {
            try
            {
                ValidateMessage(message);
                bool result = await _vaultService.Verify(Guid.Parse(vaultId), Guid.Parse(keyId), message, signature);
                return ResultWrapper<bool>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<bool>.Fail(e);
            }
        }

        public async Task<ResultWrapper<bool>> vault_setToken(string token)
        {
            try
            {
                await _vaultService.ResetToken(token);
                return ResultWrapper<bool>.Success(true);
            }
            catch (Exception e)
            {
                return ResultWrapper<bool>.Fail(e);
            }
        }

        public async Task<ResultWrapper<bool>> vault_configure(string scheme, string host, string path, string token)
        {
            try
            {
                await _vaultService.Reset(scheme, host, path, token);
                return ResultWrapper<bool>.Success(true);
            }
            catch (Exception e)
            {
                return ResultWrapper<bool>.Fail(e);
            }
        }

        private void ValidateMessage(string message)
        {
            if (message.StartsWith("0x"))
            {
                throw new ArgumentException($"Vault message should not be in hex; message: {message}");
            }
        }
    }
}
