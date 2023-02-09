// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using provide.Model.Vault;

namespace Nethermind.Vault
{
    public interface IVaultService
    {
        Task<IEnumerable<Guid>> ListVaultIds();

        Task<provide.Model.Vault.Vault> CreateVault(provide.Model.Vault.Vault vault);

        Task<provide.Model.Vault.Vault> DeleteVault(Guid vaultId);

        Task<IEnumerable<Key>> ListKeys(Guid vaultId);

        Task<Key> CreateKey(Guid vaultId, Key key);

        Task DeleteKey(Guid vaultId, Guid keyId);

        Task<IEnumerable<Secret>> ListSecrets(Guid vaultId);

        Task<Secret> CreateSecret(Guid vaultId, Secret secret);

        Task DeleteSecret(Guid vaultId, Guid secretId);

        Task<string> Sign(Guid vaultId, Guid keyId, string message);

        Task<bool> Verify(Guid vaultId, Guid keyId, string message, string signature);

        Task Reset(string scheme, string host, string path, string token);

        Task ResetToken(string token);
    }
}
