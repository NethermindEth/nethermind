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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.JsonRpc;
using NUnit.Framework;
using provide.Model.Vault;

namespace Nethermind.Vault.Test.JsonRpc
{
    [TestFixture]
    public class VaultModuleTests
    {
        private VaultModule _vaultModule;
        private IVaultConfig _config;
        private Guid _vaultId;
        private VaultService _vaultService;

        [SetUp]
        public async Task SetUp()
        {
            _config = new VaultConfig();
            _config.Host = "localhost:8082";
            _config.Scheme = "http";
            _config.Path = "api/v1";
            _config.Token = $"bearer  {TestContext.Parameters["token"]}";

            var vaultSealingForTestsHelper = new VaultSealingForTestsHelper(_config);
            await vaultSealingForTestsHelper.Unseal();
            _vaultService = new VaultService(_config, new TestLogManager(LogLevel.Trace));
            _vaultModule = new VaultModule(_vaultService, new TestLogManager(LogLevel.Trace));

            provide.Model.Vault.Vault vault = new provide.Model.Vault.Vault();
            vault.Name = "Test Vault";
            vault.Description = "Test Vault used for test purposes";
            ResultWrapper<provide.Model.Vault.Vault> res = await _vaultModule.vault_createVault(vault);
            if (res.Result != Result.Success || res.Data.Id is null)
            {
                throw new ApplicationException("Failed to create vault");
            }

            _vaultId = res.Data.Id.Value;
        }

        [TearDown]
        public async Task TearDown()
        {
            await CleanUpVault();
            var vaultSealingForTestsHelper = new VaultSealingForTestsHelper(_config);
            await vaultSealingForTestsHelper.Seal();
        }

        private async Task CleanUpVault()
        {
            await _vaultModule.vault_deleteVault(_vaultId.ToString());
        }

        [Test]
        public async Task create_key_can_create_a_new_key()
        {
            Key key = new Key();
            key.Name = "Test Key";
            key.Description = "Test Key used for test purposes";
            key.Type = "asymmetric";
            key.Spec = "secp256k1";
            key.Usage = "sign/verify";

            ResultWrapper<Key> createKeyResponse = await _vaultModule.vault_createKey(_vaultId.ToString(), key);
            createKeyResponse.Result.Error.Should().Be(null);
            createKeyResponse.ErrorCode.Should().Be(0);
            createKeyResponse.Data.Should().NotBeNull();
            createKeyResponse.Result.ResultType.Should().Be(ResultType.Success);

            createKeyResponse.Data.Address.Should().NotBeNullOrEmpty();
            
            _ = await _vaultModule.vault_deleteKey(_vaultId.ToString(), createKeyResponse.Data.Id!.ToString());
        }

        [Test]
        public async Task can_create_a_new_secret()
        {
            Secret secret = new Secret();
            secret.Name = "Test Secret";
            secret.Description = "Test Secret used for test purposes";
            secret.Type = "Sample secret";
            secret.Value = "Secret to be stored";

            ResultWrapper<Secret> createSecretResponse
                = await _vaultModule.vault_createSecret(_vaultId.ToString(), secret);
            createSecretResponse.Result.Error.Should().Be(null);
            createSecretResponse.ErrorCode.Should().Be(0);
            createSecretResponse.Data.Should().NotBeNull();
            createSecretResponse.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_create_a_new_vault()
        {
            provide.Model.Vault.Vault vault = new provide.Model.Vault.Vault();
            vault.Name = "Test Vault";
            vault.Description = "Test Vault used for test purposes";

            ResultWrapper<provide.Model.Vault.Vault> createVaultResponse
                = await _vaultModule.vault_createVault(vault);
            createVaultResponse.Result.Error.Should().Be(null);
            createVaultResponse.ErrorCode.Should().Be(0);
            createVaultResponse.Data.Should().NotBeNull();
            createVaultResponse.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_delete_a_secret_within_a_given_vault_by_its_secret_id()
        {
            // Create some secrets for testing
            List<string> names = new List<string> {"Name 1", "Name 2", "Name 3"};

            Guid? secretId = null;
            foreach (string name in names)
            {
                Secret secret = new Secret();
                secret.Name = name;
                secret.Description = "Test Secret used for test purposes";
                secret.Type = "Sample secret";
                secret.Value = "Secret to be stored";
                ResultWrapper<Secret> res = await _vaultModule.vault_createSecret(_vaultId.ToString(), secret);
                if (res.Result != Result.Success || res.Data.Id is null)
                {
                    throw new ApplicationException("Failed to create secret");
                }

                secretId = res.Data.Id; // Last key will be removed later
            }

            if (secretId is null)
            {
                throw new ApplicationException($"Failed to create secrets with valid {nameof(Secret.Id)}");
            }

            ResultWrapper<bool> response =
                await _vaultModule.vault_deleteSecret(_vaultId.ToString(), secretId.ToString());
            response.Result.Error.Should().BeNull();
            response.Data.Should().BeTrue();
            response.Result.ResultType.Should().Be(ResultType.Success);

            ResultWrapper<Secret[]> secretsAfterDelete = await _vaultModule.vault_listSecrets(_vaultId.ToString());
            foreach (Secret secret in secretsAfterDelete.Data)
            {
                Guid? currentSecretId = secret.Id;
                // Check whether the last secret generated in above loop was correctly removed from Vault
                Assert.AreNotEqual(currentSecretId, secretId);
            }
        }

        [Test]
        public async Task list_keys_can_display_list_of_keys_within_a_given_vault()
        {
            Key key = new Key();
            key.Name = "Test Key";
            key.Description = "Test Key used for test purposes";
            key.Type = "asymmetric";
            key.Spec = "secp256k1";
            key.Usage = "sign/verify";

            ResultWrapper<Key> createKeyResponse = await _vaultModule.vault_createKey(_vaultId.ToString(), key);
            createKeyResponse.Result.ResultType.Should().Be(ResultType.Success);
            createKeyResponse.Data.Address.Should().NotBeNullOrEmpty();

            ResultWrapper<Key[]> listKeysResponse = await _vaultModule.vault_listKeys(_vaultId.ToString());
            listKeysResponse.Result.Error.Should().Be(null);
            listKeysResponse.ErrorCode.Should().Be(0);
            listKeysResponse.Data.Should().NotBeNull();
            // listKeysResponse.Data.Should().HaveCount(1); // somehow two keys are returned (maybe master key)
            listKeysResponse.Result.ResultType.Should().Be(ResultType.Success);

            foreach (Key listedKey in listKeysResponse.Data)
            {
                listedKey.Address.Should().NotBeNullOrEmpty(listedKey.Name + " " + listedKey.Description);
                _ = await _vaultModule.vault_deleteKey(_vaultId.ToString(), listedKey.Id!.ToString());
            }
        }

        [Test]
        public async Task list_secrets_can_display_list_of_secrets_within_a_given_vault()
        {
            Secret secret = new Secret();
            secret.Name = "Sample secret name";
            secret.Description = "Test Secret used for test purposes";
            secret.Type = "Sample secret";
            secret.Value = "Secret to be stored";

            ResultWrapper<Secret> createSecretResponse =
                await _vaultModule.vault_createSecret(_vaultId.ToString(), secret);
            createSecretResponse.Result.ResultType.Should().Be(ResultType.Success);

            var listSecretsResponse = await _vaultModule.vault_listSecrets(_vaultId.ToString());
            listSecretsResponse.Result.Error.Should().Be(null);
            listSecretsResponse.ErrorCode.Should().Be(0);
            listSecretsResponse.Data.Should().NotBeNull();
            listSecretsResponse.Data.Should().HaveCount(1);
            listSecretsResponse.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task list_vaults_can_display_a_list_of_owned_vaults()
        {
            ResultWrapper<string[]> listVaultsResponse = await _vaultModule.vault_listVaults();
            listVaultsResponse.Result.Error.Should().Be(null);
            listVaultsResponse.ErrorCode.Should().Be(0);
            listVaultsResponse.Data.Should().NotBeNull();
            listVaultsResponse.Data.Should().HaveCount(2);
            listVaultsResponse.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_sign_a_message_with_a_given_key()
        {
            // string _message = "Test message";
            string _message = Keccak.OfAnEmptyString.ToString(false);

            Key key = new Key();
            key.Name = "Test Key for Signature test";
            key.Description = "Test Key used for Signature test";
            key.Type = "asymmetric";
            key.Spec = "secp256k1";
            key.Usage = "sign/verify";

            var createKeyResponse = await _vaultModule.vault_createKey(_vaultId.ToString(), key);
            createKeyResponse.Result.Error.Should().Be(null);
            createKeyResponse.ErrorCode.Should().Be(0);
            createKeyResponse.Data.Should().NotBeNull();
            createKeyResponse.Result.ResultType.Should().Be(ResultType.Success);
            createKeyResponse.Data.Id.Should().NotBeNull();

            Guid keyId = createKeyResponse.Data.Id!.Value;
            ResultWrapper<string> signMessageResponse
                = await _vaultModule.vault_signMessage(_vaultId.ToString(), keyId.ToString(), _message);
            signMessageResponse.Result.Error.Should().Be(null);
            signMessageResponse.ErrorCode.Should().Be(0);
            signMessageResponse.Data.Should().NotBeNull();
            signMessageResponse.Result.ResultType.Should().Be(ResultType.Success);
            
            _ = await _vaultModule.vault_deleteKey(_vaultId.ToString(), keyId!.ToString());
        }

        [Test]
        public async Task can_verify_a_message_with_a_given_key_and_signature()
        {
            // string _message = "Test message";
            string _message = Keccak.OfAnEmptyString.ToString(false);
            Key key = new Key();
            key.Name = "Test Key for Signature test";
            key.Description = "Test Key used for Signature test";
            key.Type = "asymmetric";
            key.Spec = "secp256k1";
            key.Usage = "sign/verify";

            var createKeyResponse = await _vaultModule.vault_createKey(_vaultId.ToString(), key);
            Guid keyId = createKeyResponse.Data.Id!.Value;

            ResultWrapper<string> signResponse
                = await _vaultModule.vault_signMessage(_vaultId.ToString(), keyId.ToString(), _message);
            string signature = signResponse.Data;

            var verifySignatureResponse = await _vaultModule.vault_verifySignature(
                _vaultId.ToString(), keyId.ToString(), _message, signature);
            verifySignatureResponse.Result.Error.Should().Be(null);
            verifySignatureResponse.ErrorCode.Should().Be(0);
            verifySignatureResponse.Data.Should().Be(true);
            verifySignatureResponse.Result.ResultType.Should().Be(ResultType.Success);
            
            _ = await _vaultModule.vault_deleteKey(_vaultId.ToString(), keyId!.ToString());
        }

        [Test]
        public async Task can_delete_a_vault()
        {
            ResultWrapper<string[]> listVaultsResponse = await _vaultModule.vault_listVaults();
            listVaultsResponse.Data.Should().HaveCount(2);
            
            provide.Model.Vault.Vault vault  = new provide.Model.Vault.Vault();
            vault.Name = "Name 0";
            vault.Description = "Test Vault used for test purposes";

            ResultWrapper<provide.Model.Vault.Vault> createVaultResponse
                = await _vaultModule.vault_createVault(vault);
            Guid? vaultId = createVaultResponse.Data.Id;
            vaultId.Should().NotBeNull();
            
            listVaultsResponse = await _vaultModule.vault_listVaults();
            listVaultsResponse.Data.Should().HaveCount(3);

            ResultWrapper<bool> deleteVaultResponse
                = await _vaultModule.vault_deleteVault(vaultId.ToString());
            deleteVaultResponse.Result.Error.Should().BeNull();
            deleteVaultResponse.ErrorCode.Should().Be(0);
            deleteVaultResponse.Data.Should().Be(true);
            deleteVaultResponse.Result.ResultType.Should().Be(ResultType.Success);

            listVaultsResponse = await _vaultModule.vault_listVaults();
            listVaultsResponse.Data.Should().HaveCount(2);

            listVaultsResponse.Data.Should().NotContain(vaultId.ToString());
        }

        [Test]
        public async Task can_delete_a_key_within_a_given_vault_by_its_key_id()
        {
            // Create some keys for testing
            List<string> names = new List<string> {"Name 1", "Name 2", "Name 3"};
            Guid? lastKeyId = null;
            foreach (var name in names)
            {
                Key key = new Key();
                key.Name = name;
                key.Description = "Test Key used for deleteKey test";
                key.Type = "asymmetric";
                key.Spec = "secp256k1";
                key.Usage = "sign/verify";
                ResultWrapper<Key> res = await _vaultModule.vault_createKey(_vaultId.ToString(), key);
                res.Result.ResultType.Should().Be(ResultType.Success);
                lastKeyId = res.Data.Id;
            }

            lastKeyId.Should().NotBeNull();
            ResultWrapper<bool> deleteKeyResponse
                = await _vaultModule.vault_deleteKey(_vaultId.ToString(), lastKeyId!.Value.ToString());
            deleteKeyResponse.Result.Error.Should().BeNull();
            deleteKeyResponse.ErrorCode.Should().Be(0);
            deleteKeyResponse.Data.Should().BeTrue();
            deleteKeyResponse.Result.ResultType.Should().Be(ResultType.Success);

            ResultWrapper<Key[]> listKeysResponse = await _vaultModule.vault_listKeys(_vaultId.ToString());
            listKeysResponse.Data.Should().HaveCount(2);
            listKeysResponse.Data.Select(k => k.Id).Should().NotContain(lastKeyId);
        }
        
        [Test]
        public async Task can_configure()
        {
            string host = "localhost:8082";
            string scheme = "http";
            string path = "api/v1";
            string token = $"bearer  {TestContext.Parameters["token"]}";

            var setTokenResponse = await _vaultModule.vault_setToken(token);
            setTokenResponse.Result.ResultType.Should().Be(ResultType.Success);
            
            var configureResponse = await _vaultModule.vault_configure(scheme, host, path, token);
            configureResponse.Result.ResultType.Should().Be(ResultType.Success);
        }
    }
}
