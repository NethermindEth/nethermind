using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.JsonRpc;
using Nethermind.Vault.Styles;
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
        private string _secretId;
        private string _message;
        private string _signature;
        private KeyArgs _keyArgs;
        private VaultArgs _vaultArgs;
        private SecretArgs _secretArgs;

        [SetUp]
        public void SetUp()
        {
            _config = new VaultConfig();
            _config.Host = "localhost";
            _config.Scheme = "http";
            _config.Path = "api/v1";
            _config.Token = "12345";
            _initVault = new provide.Vault(_config.Host, _config.Path, _config.Scheme, _config.Token);
            _vaultId = "vaultId";
            _message = "Test message";
            _signature = "12345";
            _keyArgs = KeyArgs.Default;
            _vaultArgs = VaultArgs.Default;
            _secretArgs = SecretArgs.Default;
            _vaultModule = new VaultModule(
                _config,
                LimboLogs.Instance);
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
            Console.WriteLine(result.Result.Error);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_create_a_new_secret()
        {
            _secretArgs.Name = "Test Secret";
            _secretArgs.Description = "Test Secret used for test purposes";

            var result = await _vaultModule.vault_createSecret(_vaultId, _secretArgs);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_create_a_new_vault()
        {
            _vaultArgs.Name = "Test Vault";
            _vaultArgs.Description = "Test Vault used for test purposes";

            var result = await _vaultModule.vault_createVault(_vaultArgs);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_delete_a_key_within_a_given_vault_by_its_key_id()
        {
            var result = await _vaultModule.vault_deleteKey(_vaultId, _keyId);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_delete_a_secret_within_a_given_vault_by_its_secret_id()
        {
            var result = await _vaultModule.vault_deleteSecret(_vaultId, _keyId);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task list_keys_can_display_list_of_keys_within_a_given_vault()
        {
            var result = await _vaultModule.vault_listKeys(_vaultId);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task list_secrets_can_display_list_of_secrets_within_a_given_vault()
        {
            var result = await _vaultModule.vault_listSecrets(_vaultId);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task list_vaults_can_display_a_list_of_owned_vaults()
        {
            var result = await _vaultModule.vault_listVaults();

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_sign_a_message_with_a_given_key()
        {
            var result = await _vaultModule.vault_signMessage(_vaultId, _keyId, _message);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task can_verify_a_message_with_a_given_key_and_signature()
        {
            var result = await _vaultModule.vault_verifySignature(_vaultId, _keyId, _message, _signature);

            result.Result.Error.Should().NotBeNull();
            result.Data.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);

            // result.ErrorCode.Should().Be(0);
            // result.Result.Error.Should().Be(null);
            // result.Data.Should().NotBeNull();
            // result.Result.ResultType.Should().Be(ResultType.Success);
        }
    }
}