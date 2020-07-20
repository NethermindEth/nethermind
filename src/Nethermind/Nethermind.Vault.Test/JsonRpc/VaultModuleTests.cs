using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.JsonRpc;
using Nethermind.Vault.Styles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.Vault.Test.JsonRpc
{
    [TestFixture]
    public class VaultModuleTests
    {
        private provide.Vault _initVault;
        private VaultModule _vaultModule;
        private IVaultConfig _config;
        private string _vaultId;
        private string _keyId;
        // private string _secretId;
        private KeyArgs _keyArgs;
        private VaultArgs _vaultArgs;
        private SecretArgs _secretArgs;
        // private VaultManager _vaultManager;

        [OneTimeSetUp]
        public async Task SetUp()
        {
            _config = new VaultConfig();
            _config.Host = "localhost:8082";
            _config.Scheme = "http";
            _config.Path = "api/v1";
            _config.Token = $"bearer  {TestContext.Parameters["token"]}";
            _initVault = new provide.Vault(_config.Host, _config.Path, _config.Scheme, _config.Token);
            _keyArgs = KeyArgs.Default;
            _vaultArgs = VaultArgs.Default;
            _secretArgs = SecretArgs.Default;
            _vaultModule = new VaultModule(
                _config,
                LimboLogs.Instance);

            
            _vaultArgs.Name = "Test Vault";
            _vaultArgs.Description = "Test Vault used for test purposes";
            var res  = await _vaultModule.vault_createVault(_vaultArgs);
            dynamic vault = JObject.Parse(res.Data.ToString());
            _vaultId = vault.id;
        }


        [OneTimeTearDown]
        public async Task TearDown()
        {   
            var vaultsToBeDeleted = await _vaultModule.vault_listVaults();
            JArray vaults = JArray.Parse(vaultsToBeDeleted.Data.ToString());
            foreach (var v in vaults)
            {
                dynamic vault = JObject.Parse(v.ToString());
                string vaultId = vault.id;
                await _vaultModule.vault_deleteVault(vaultId); 
            }
            // delete initial vault as well
            await _vaultModule.vault_deleteVault(_vaultId);
        }

        [Test]
        public async Task create_key_can_create_a_new_key()
        {
            _keyArgs.Name = "Test Key";
            _keyArgs.Description = "Test Key used for test purposes";
            _keyArgs.Type = "asymmetric";
            _keyArgs.Spec = "secp256k1";
            _keyArgs.Usage = "sign/verify";

            var result = await _vaultModule.vault_createKey(_vaultId, _keyArgs);

            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        [Ignore("Secrets not available in Vault at the moment")]
        public async Task can_create_a_new_secret()
        {
            _secretArgs.Name = "Test Secret";
            _secretArgs.Description = "Test Secret used for test purposes";

            var result = await _vaultModule.vault_createSecret(_vaultId, _secretArgs);

            Console.WriteLine(result.Result.Error);

            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_create_a_new_vault()
        {
            _vaultArgs.Name = "Test Vault";
            _vaultArgs.Description = "Test Vault used for test purposes";

            var result = await _vaultModule.vault_createVault(_vaultArgs);
            
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        [Ignore("Secrets not available in Vault at the moment")]
        public async Task can_delete_a_secret_within_a_given_vault_by_its_secret_id()
        {
            var result = await _vaultModule.vault_deleteSecret(_vaultId, _keyId);

            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task list_keys_can_display_list_of_keys_within_a_given_vault()
        {
            var result = await _vaultModule.vault_listKeys(_vaultId);

            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        [Ignore("Secrets not available in Vault at the moment")]
        public async Task list_secrets_can_display_list_of_secrets_within_a_given_vault()
        {
            var result = await _vaultModule.vault_listSecrets(_vaultId);

            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task list_vaults_can_display_a_list_of_owned_vaults()
        {
            var result = await _vaultModule.vault_listVaults();

            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_sign_a_message_with_a_given_key()
        {
            string _message = "Test message";

            _keyArgs.Name = "Test Key for Signature test";
            _keyArgs.Description = "Test Key used for Signature test";
            _keyArgs.Type = "asymmetric";
            _keyArgs.Spec = "secp256k1";
            _keyArgs.Usage = "sign/verify";

            var res = await _vaultModule.vault_createKey(_vaultId, _keyArgs);
            dynamic key = JObject.Parse(res.Data.ToString());
            _keyId = key.id;

            var result = await _vaultModule.vault_signMessage(_vaultId, _keyId, _message);
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_verify_a_message_with_a_given_key_and_signature()
        {
            string _message = "Test message";
            _keyArgs.Name = "Test Key for Signature test";
            _keyArgs.Description = "Test Key used for Signature test";
            _keyArgs.Type = "asymmetric";
            _keyArgs.Spec = "secp256k1";
            _keyArgs.Usage = "sign/verify";

            var res = await _vaultModule.vault_createKey(_vaultId, _keyArgs);
            dynamic key = JObject.Parse(res.Data.ToString());
            _keyId = key.id;

            var resSignature = await _vaultModule.vault_signMessage(_vaultId, _keyId, _message);
            dynamic sig = JObject.Parse(resSignature.Data.ToString());
            string _signature = sig.signature;
        
            var result = await _vaultModule.vault_verifySignature(_vaultId, _keyId, _message, _signature);
            dynamic verifier = JObject.Parse(result.Data.ToString());

            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().Be(null);
            result.Data.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);
            Assert.IsTrue(verifier.verified == true);
        }

        [Test]
        public async Task can_delete_a_vault()
        {
            string vaultIdTest;
            _vaultArgs.Name = "Name 0";
            _vaultArgs.Description = "Test Vault used for test purposes";

            var res  = await _vaultModule.vault_createVault(_vaultArgs);
            dynamic vault = JObject.Parse(res.Data.ToString());
            vaultIdTest = vault.id;

            var result = await _vaultModule.vault_deleteVault(vaultIdTest);

            var vaultsAfterDelete = await _vaultModule.vault_listVaults();
            JArray vaults = JArray.Parse(vaultsAfterDelete.Data.ToString());
            foreach (var v in vaults)
            {
                dynamic val = JObject.Parse(v.ToString());
                string vaultId = val.id;
                // Check whether the last vault generated in above loop was correctly removed from Vault
                Assert.AreNotEqual(vaultId, vaultIdTest);
            }
            
            result.Result.Error.Should().BeEmpty();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
        }

        [Test]
        public async Task can_delete_a_key_within_a_given_vault_by_its_key_id()
        {
            // Create some keys for testing
            List<string> names = new List<string> {"Name 1", "Name 2", "Name 3"};
            
            foreach (var name in names)
            {
                _keyArgs.Name = name;
                _keyArgs.Description = "Test Key used for deleteKey test";
                _keyArgs.Type = "asymmetric";
                _keyArgs.Spec = "secp256k1";
                _keyArgs.Usage = "sign/verify";
                var res = await _vaultModule.vault_createKey(_vaultId, _keyArgs);
                dynamic key = JObject.Parse(res.Data.ToString());
                _keyId = key.id; // Last key will be removed later
            }

            var result = await _vaultModule.vault_deleteKey(_vaultId, _keyId);
            
            var keysAfterDelete = await _vaultModule.vault_listKeys(_vaultId);
            JArray keys = JArray.Parse(keysAfterDelete.Data.ToString());
            foreach (var k in keys)
            {
                dynamic key = JObject.Parse(k.ToString());
                string keyId = key.id;
                // Check whether the last key generated in above loop was correctly removed from Vault
                Assert.AreNotEqual(keyId, _keyId);
            }
            
            result.Result.Error.Should().BeEmpty();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
        }
    }
}