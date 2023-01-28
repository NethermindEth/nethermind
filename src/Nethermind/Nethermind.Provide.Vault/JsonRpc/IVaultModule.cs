// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using provide.Model.Vault;

namespace Nethermind.Vault.JsonRpc
{
    [RpcModule(ModuleType.Vault)]
    public interface IVaultModule : IRpcModule
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
