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
using Nethermind.Vault.Config;
using System.Collections.Generic;
using Newtonsoft.Json;
using Nethermind.Vault.Styles;

namespace Nethermind.Vault.JsonRpc
{
    public class VaultModule : IVaultModule
    {

        private readonly ILogger _logger;
        private readonly IVaultConfig _vaultConfig;
        private readonly provide.Vault _initVault;

        public VaultModule(IVaultConfig vaultConfig, ILogManager logManager)
        
        {
            _vaultConfig = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _initVault = new provide.Vault(_vaultConfig.Host, _vaultConfig.Path, _vaultConfig.Scheme , _vaultConfig.Token);
        }

        public async Task<ResultWrapper<object>> vault_createKey(string vaultId, KeyArgs args )
        {
            try 
            {
                var result = await _initVault.CreateVaultKey(_vaultConfig.Token, vaultId, args.ToDictionary());   

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_createSecret(string vaultId, SecretArgs args)
        {
            try 
            {
                var result = await _initVault.CreateVaultSecret(_vaultConfig.Token, vaultId, args.ToDictionary());   

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_createVault(VaultArgs args)
        {
            try 
            {
                var result = await _initVault.CreateVault(_vaultConfig.Token, args.ToDictionary()); 

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_deleteVault(string vaultId)
        {
            try 
            {
                var result = await _initVault.DeleteVault(_vaultConfig.Token, vaultId); 

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_deleteKey(string vaultId, string keyId)
        {
            try 
            {
                var result = await _initVault.DeleteVaultKey(_vaultConfig.Token, vaultId, keyId); 

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_deleteSecret(string vaultId, string secretId)
        {
            try 
            {
                var result = await _initVault.DeleteVaultSecret(_vaultConfig.Token, vaultId, secretId); 

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_listKeys(string vaultId)
        {
            try 
            {
                var args = new Dictionary<string, object> {};
                var result = await _initVault.ListVaultKeys(_vaultConfig.Token, vaultId, args);   

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_listSecrets(string vaultId)
        {
            try 
            {
                var args = new Dictionary<string, object> {};
                var result = await _initVault.ListVaultSecrets(_vaultConfig.Token, vaultId, args);   

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_listVaults()
        {
            try 
            {
                var args = new Dictionary<string, object> {};
                var result = await _initVault.ListVaults(_vaultConfig.Token, args);

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_signMessage(string vaultId, string keyId, string message)
        {
            try 
            {
                var args = new Dictionary<string, object> {};
                var result = await _initVault.SignMessage(_vaultConfig.Token, vaultId, keyId, message);   

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        public async Task<ResultWrapper<object>> vault_verifySignature(string vaultId, string keyId, string message, string signature)
        {
            try 
            {
                var args = new Dictionary<string, object> {};
                var result = await _initVault.VerifySignature(_vaultConfig.Token, vaultId, keyId, message, signature);   

                return ReturnResult(result);
            } 
            catch (Exception e) 
            {
                return ResultWrapper<object>.Fail(e.ToString());
            }
        }

        private ResultWrapper<object> ReturnResult((int, string) result)
        {
            if (result.Item1 == 200 || result.Item1 == 201)
            {
                dynamic jsonResult  = JsonConvert.DeserializeObject(result.Item2);
                return ResultWrapper<object>.Success(jsonResult);
            }
            else
            {
                return ResultWrapper<object>.Fail(result.Item2);
            }
        }
    }
}
