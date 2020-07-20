using FluentAssertions;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.Styles;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Vault.Test
{
    [TestFixture]
    public class VaultManagerTests
    {
        private IVaultConfig _config;
        private VaultManager _vaultManager;

        public TestContext TestContext { get; set; }

        [OneTimeSetUp]
        public void SetUp()
        {
            _config = new VaultConfig();
            _config.Host = "localhost:8082";
            _config.Scheme = "http";
            _config.Path = "api/v1";
            _config.Token = $"bearer  {TestContext.Parameters["token"]}";
            _vaultManager = new VaultManager(
                _config,
                LimboLogs.Instance
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            var vaults = await _vaultManager.GetVaults();
            foreach (var vault in vaults)
            {
                await _vaultManager.DeleteVault(vault);
            }      
        }

        [Test]
        public async Task can_return_a_list_of_vaults()
        {
            VaultArgs args = null;
            Dictionary<string, object> parameters = new Dictionary<string,object> 
            {
                {
                    "vaultArgs", args
                }
            };
            var vault = await _vaultManager.NewVault(parameters);

            var result = await _vaultManager.GetVaults();

            result.Should().NotBeNull();
            result.Should().Contain(vault);
        }


        [Test]
        public async Task can_create_a_new_vault()
        {
            VaultArgs args = new VaultArgs();
            args.Name = "Wallet Vault Test";
            args.Description = "Test Vault used for test purposes";

            Dictionary<string, object> parameters = new Dictionary<string,object> 
            {
                {
                    "vaultArgs", args
                }
            };

            var result = await _vaultManager.NewVault(parameters);

            result.Should().NotBeNull();
        }


        [Test]
        public async Task can_delete_vault()
        {
            VaultArgs args = null;
            Dictionary<string, object> parameters = new Dictionary<string,object> 
            {
                {
                    "vaultArgs", args
                }
            };
            var vaultId = await _vaultManager.NewVault(parameters);

            await _vaultManager.DeleteVault(vaultId);

            var vaults = await _vaultManager.GetVaults();

            vaults.Should().NotContain(vaultId);
        }
    }
}