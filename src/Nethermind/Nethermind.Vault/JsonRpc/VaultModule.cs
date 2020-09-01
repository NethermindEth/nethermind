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
using Nethermind.Logging;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Vault.Styles;
using provide.Model.Vault;

namespace Nethermind.Vault.JsonRpc
{
    public class VaultModule : IVaultModule
    {
        private readonly IVaultService _vaultService;

        private readonly ILogger _logger;

        public VaultModule(IVaultService vaultService, ILogManager logManager)
        {
            _vaultService = vaultService ?? throw new ArgumentNullException(nameof(vaultService));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<ResultWrapper<Key>> vault_createKey(string vaultId, Key key)
        {
            Key result = await _vaultService.CreateKey(vaultId, key);
            return ResultWrapper<Key>.Success(result);
        }

        public async Task<ResultWrapper<Secret>> vault_createSecret(string vaultId, Secret secret)
        {
            try
            {
                Secret result = await _vaultService.CreateSecret(vaultId, secret);
                return ResultWrapper<Secret>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<Secret>.Fail(e.ToString());
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
                return ResultWrapper<provide.Model.Vault.Vault>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<provide.Model.Vault.Vault>> vault_deleteVault(string vaultId)
        {
            try
            {
                provide.Model.Vault.Vault result = await _vaultService.DeleteVault(vaultId);
                return ResultWrapper<provide.Model.Vault.Vault>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<provide.Model.Vault.Vault>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<Key>> vault_deleteKey(string vaultId, string keyId)
        {
            try
            {
                Key result = await _vaultService.DeleteKey(vaultId, keyId);
                return ResultWrapper<Key>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<Key>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<Secret>> vault_deleteSecret(string vaultId, string secretId)
        {
            try
            {
                var result = await _vaultService.DeleteSecret(vaultId, secretId);

                return ResultWrapper<Secret>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<Secret>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<Key[]>> vault_listKeys(string vaultId)
        {
            try
            {
                var result = await _vaultService.ListKeys(vaultId);
                return ResultWrapper<Key[]>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<Key[]>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<Secret[]>> vault_listSecrets(string vaultId)
        {
            try
            {
                Secret[] result = await _vaultService.ListSecrets(vaultId);
                return ResultWrapper<Secret[]>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<Secret[]>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<string[]>> vault_listVaults()
        {
            try
            {
                var result = await _vaultService.ListVaultIds();

                return ResultWrapper<string[]>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<string[]>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<string>> vault_signMessage(string vaultId, string keyId, string message)
        {
            try
            {
                string result = await _vaultService.Sign(vaultId, keyId, message);
                return ResultWrapper<string>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<string>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<bool>> vault_verifySignature(string vaultId, string keyId, string message, string signature)
        {
            try
            {
                bool result = await _vaultService.Verify(vaultId, keyId, message, signature);
                return ResultWrapper<bool>.Success(result);
            }
            catch (Exception e)
            {
                return ResultWrapper<bool>.Fail(e.ToString());
            }
        }
    }
}