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

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Vault.Styles;

namespace Nethermind.Vault.JsonRpc
{
    [RpcModule(ModuleType.Vault)]
    public interface IVaultModule: IModule
    {
        [JsonRpcMethod(
            Description = "Creates a Vault",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_createVault(VaultArgs args);

        [JsonRpcMethod(
            Description = "Deletes a Vault",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_deleteVault(string vaultId);

        [JsonRpcMethod(
            Description = "Displays a list of Vaults",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_listVaults();

        [JsonRpcMethod(
            Description = "Displays a list of keys in a single Vault",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_listKeys(string vaultId);

        [JsonRpcMethod(
            Description = "Generates a new symmetric key or asymmetric keypair",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_createKey(string vaultId, KeyArgs args);

        [JsonRpcMethod(
            Description = "Deletes a key from Vault",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_deleteKey(string vaultId, string keyId);

        [JsonRpcMethod(
            Description = "Retrieves a list of the secrets secured within the vault",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_listSecrets(string vaultId);

        [JsonRpcMethod(
            Description = "Creates a new secret within the vault",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_createSecret(string vaultId, SecretArgs args);

        [JsonRpcMethod(
            Description = "Permanently removes the specified secret from the vault",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_deleteSecret(string vaultId, string secretId);

        [JsonRpcMethod(
            Description = "Securely signs the given message",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_signMessage(string vaultId, string keyId, string message);

        [JsonRpcMethod(
            Description = "Verifies that a message was previously signed with a given key",
            IsReadOnly = false,
            IsImplemented = true)]
        Task<ResultWrapper<object>> vault_verifySignature(string vaultId, string keyId, string message, string signature);
    }
}