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

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using provide.Model.Vault;

namespace Nethermind.Vault.JsonRpc
{
    [RpcModule(ModuleType.Vault)]
    public interface IVaultModule: IRpcModule
    {
        [JsonRpcMethod(
            Description = "Displays a list of Vaults",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<string[]>> vault_listVaults();
        
        [JsonRpcMethod(
            Description = "Creates a Vault",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<provide.Model.Vault.Vault>> vault_createVault(provide.Model.Vault.Vault args);

        [JsonRpcMethod(
            Description = "Deletes a Vault",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> vault_deleteVault(string vaultId);

        [JsonRpcMethod(
            Description = "Displays a list of keys in a single Vault",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<Key[]>> vault_listKeys(string vaultId);

        [JsonRpcMethod(
            Description = "Generates a new symmetric key or asymmetric keypair",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<Key>> vault_createKey(string vaultId, Key args);

        [JsonRpcMethod(
            Description = "Deletes a key from Vault",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> vault_deleteKey(string vaultId, string keyId);

        [JsonRpcMethod(
            Description = "Retrieves a list of the secrets secured within the vault",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<Secret[]>> vault_listSecrets(string vaultId);

        [JsonRpcMethod(
            Description = "Creates a new secret within the vault",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<Secret>> vault_createSecret(string vaultId, Secret args);

        [JsonRpcMethod(
            Description = "Permanently removes the specified secret from the vault",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> vault_deleteSecret(string vaultId, string secretId);

        [JsonRpcMethod(
            Description = "Securely signs the given message",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<string>> vault_signMessage(string vaultId, string keyId, string message);

        [JsonRpcMethod(
            Description = "Verifies that a message was previously signed with a given key",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> vault_verifySignature(string vaultId, string keyId, string message, string signature);
        
        [JsonRpcMethod(
            Description = "Sets the API token used when talking to the Vault Service",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> vault_setToken(string token);

        [JsonRpcMethod(
            Description = "Sets the API token and Vault Service configuration to use",
            IsSharable = false,
            IsImplemented = true)]
        Task<ResultWrapper<bool>> vault_configure(string scheme, string host, string path, string token);
    }
}
