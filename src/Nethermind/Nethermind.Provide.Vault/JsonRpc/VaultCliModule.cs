//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using Nethermind.Cli;
using Nethermind.Cli.Modules;
using provide.Model.Vault;

namespace Nethermind.Vault.JsonRpc
{
    [CliModule("vault")]
    public class VaultCliModule : CliModuleBase
    {
        public VaultCliModule(ICliEngine engine, INodeManager nodeManager) 
            : base(engine, nodeManager)
        {
        }

        [CliFunction("vault", "listVaults")]
        public object ListVaults() => NodeManager.PostJint(
            "vault_listVaults").Result;

        [CliFunction("vault", "createVault")]
         public object CreateVault(string name, string description) => NodeManager.PostJint(
             "vault_createVault",
             new provide.Model.Vault.Vault()
             {
                 Name = name,
                 Description = description
             }).Result;

        [CliFunction("vault", "deleteVault")]
        public bool DeleteVault(string vaultId) => NodeManager.Post<bool>(
             "vault_deleteVault",
              vaultId).Result;

        [CliFunction("vault", "listKeys")]
        public object ListKeys(string vaultId) => NodeManager.PostJint(
             "vault_listKeys",
              vaultId).Result;

        [CliFunction("vault", "createKey")]
        public object CreateKey(string vaultId, string keyName, string keyDescription, string keyType) => NodeManager.PostJint(
             "vault_createKey",
              vaultId,
              new provide.Model.Vault.Key()
              {
                  Name = keyName,
                  Description = keyDescription,
                  Spec = "secp256k1",
                  Type = keyType
              }).Result;

        [CliFunction("vault", "deleteKey")]
        public bool DeleteKey(string vaultId, string keyId) => NodeManager.Post<bool>(
             "vault_deleteKey",
              vaultId,
              keyId).Result;

        [CliFunction("vault", "listSecrets")]
        public object ListSecrets(string vaultId) => NodeManager.PostJint(
             "vault_listSecrets",
              vaultId).Result;

        [CliFunction("vault", "createSecret")]
        public object CreateSecret(string vaultId, string secretName, string secretDescription, string secretType, string secretValue) => NodeManager.PostJint(
             "vault_createSecret",
              vaultId,
              new Secret()
              {
                  Name = secretName,
                  Description = secretDescription,
                  Type = secretType,
                  Value = secretValue
              }).Result;

        [CliFunction("vault", "deleteSecret")]
        public bool DeleteSecret(string vaultId, string secretId) => NodeManager.Post<bool>(
             "vault_deleteSecret",
              vaultId,
              secretId).Result;

        [CliFunction("vault", "signMessage")]
        public string SignMessage(string vaultId, string keyId, string message) => NodeManager.Post<string>(
             "vault_signMessage",
              vaultId,
              keyId,
              message).Result;

        [CliFunction("vault", "verifySignature")]
        public bool VerifySignature(string vaultId, string keyId, string message, string signature) => NodeManager.Post<bool>(
             "vault_verifySignature",
              vaultId,
              keyId,
              message,
              signature).Result;

        [CliFunction("vault", "setToken")]
        public bool SetToken(string token) => NodeManager.Post<bool>(
             "vault_setToken",
              token).Result;

        [CliFunction("vault", "configure")]
        public bool Configure(string scheme, string host, string path, string token) => NodeManager.Post<bool>(
             "vault_configure",
              scheme,
              host,
              path,
              token).Result;
    }
}
