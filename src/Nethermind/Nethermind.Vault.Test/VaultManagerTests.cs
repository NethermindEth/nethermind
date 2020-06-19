using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.Styles;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace Nethermind.Vault.Test
{
    [TestFixture]
    public class VaultManagerTests
    {
        private IVaultConfig _config;
        private VaultManager _vaultManager;


        [SetUp]
        public void SetUp()
        {
            _config = new VaultConfig();
            _config.Host = "localhost";
            _config.Scheme = "http";
            _config.Path = "api/v1";
            _config.Token = "12345";
            _vaultManager = new VaultManager(
                _config,
                LimboLogs.Instance
            );
        }

        [Test]
        public async Task can_return_a_list_of_vaults()
        {
            var result = await _vaultManager.GetVaults();

            result.Should().NotBeNull();
            result.Should().AllBeOfType<string>();
        }


        [Test]
        public async Task can_create_a_new_vault()
        {
            VaultArgs args = new VaultArgs();
            args.Name = "Wallet Vault Test";
            args.Description = "Test Vault used for test purposes";
            var result = await _vaultManager.NewVault(args);

            result.Should().NotBeNull();
            result.Should().BeOfType<string>();
        }

        [Test]
        public async Task can_set_vault_id_from_configuration()
        {
            _config.VaultId = "vaultId";
            var result = await _vaultManager.SetWalletVault(_config.VaultId);

            result.Should().NotBeNull();
            result.Should().BeOfType<string>();
            Assert.AreEqual(_config.VaultId, result);
        }

        [Test]
        public async Task can_set_default_vault_id()
        {
            var result = await _vaultManager.SetWalletVault(null);

            result.Should().NotBeNull();
            result.Should().BeOfType<string>();
        }
    }
}