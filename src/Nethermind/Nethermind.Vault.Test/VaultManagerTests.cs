using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.Styles;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
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
            _config.Token = "test";
            _vaultManager = new VaultManager(
                _config,
                LimboLogs.Instance
            );
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

            Console.WriteLine(result);
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
    }
}