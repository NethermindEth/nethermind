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


        // ToDo allow to pass more arguments
        [CliFunction("vault", "createVault")]
         public object CreateVault(string name, string description) => NodeManager.Post<string>(
             "vault_createVault",
             new provide.Model.Vault.Vault()
             {
                 Name = name,
                 Description = description
             }).Result;


        [CliFunction("vault", "deleteVault")]
        public object DeleteVault(string vaultId) => NodeManager.Post<string>(
             "vault_deleteVault",
              vaultId).Result;


        [CliFunction("vault", "listKeys")]
        public object ListKeys(string vaultId) => NodeManager.Post<string>(
             "vault_listKeys",
              vaultId).Result;

        // ToDo parse Key args
        [CliFunction("vault", "createKey")]
        public object CreateKey(string vaultId, Key args) => NodeManager.Post<string>(
             "vault_createKey",
              vaultId,
              args).Result;


        [CliFunction("vault", "deleteKey")]
        public object DeleteKey(string vaultId, string keyId) => NodeManager.Post<string>(
             "vault_deleteKey",
              vaultId,
              keyId).Result;


        [CliFunction("vault", "listSecrets")]
        public object ListSecrets(string vaultId) => NodeManager.Post<string>(
             "vault_listSecrets",
              vaultId).Result;


        // ToDo parse args secret
        [CliFunction("vault", "createSecret")]
        public object CreateSecret(string vaultId, Secret args) => NodeManager.Post<string>(
             "vault_createSecret",
              vaultId,
              args).Result;


        [CliFunction("vault", "deleteSecret")]
        public object DeleteSecret(string vaultId, string secretId) => NodeManager.Post<string>(
             "vault_deleteSecret",
              vaultId,
              secretId).Result;


        [CliFunction("vault", "signMessage")]
        public object SignMessage(string vaultId, string keyId, string message) => NodeManager.Post<string>(
             "vault_signMessage",
              vaultId,
              keyId,
              message).Result;


        [CliFunction("vault", "verifySignature")]
        public object VerifySignature(string vaultId, string keyId, string message, string signature) => NodeManager.Post<string>(
             "vault_verifySignature",
              vaultId,
              keyId,
              message,
              signature).Result;


        [CliFunction("vault", "setToken")]
        public object SetToken(string token) => NodeManager.Post<string>(
             "vault_setToken",
              token).Result;


        [CliFunction("vault", "configure")]
        public object Configure(string scheme, string host, string path, string token) => NodeManager.Post<string>(
             "vault_configure",
              scheme,
              host,
              path,
              token).Result;
    }
}
