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
    public class VaultWalletTests
    {
        private IVaultConfig _config;
        private VaultWallet _wallet;
        private VaultManager _vaultManager;
        private string _vaultId;

        public TestContext TestContext { get; set; }

        [OneTimeSetUp]
        public async Task SetUp()
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
            
            _wallet = new VaultWallet(
                _vaultManager,
                _config,
                LimboLogs.Instance
            );

            VaultArgs args = null;
            Dictionary<string, object> parameters = new Dictionary<string,object> 
            {
                {
                    "vaultArgs", args
                }
            };
            // Create a single Vault instance
            _vaultId = await _vaultManager.NewVault(parameters);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            var accounts = await _wallet.GetAccounts();
            foreach (var acc in accounts)
            {
                await _wallet.DeleteAccount(acc);
            }
            await _vaultManager.DeleteVault(_vaultId);     
        }

        [Test]
        public async Task can_return_a_list_of_accounts_for_a_given_vault()
        {
            KeyArgs args = null;
            Dictionary<string, object> parameters = new Dictionary<string,object> 
            {
                {
                    "keyArgs", args
                }
            };
            var acc = await _wallet.NewAccount(parameters);

            var result = await _wallet.GetAccounts();

            result.Should().NotBeNullOrEmpty();
            result.Should().Contain(acc);
        }


        [Test]
        public async Task can_create_a_new_account_within_a_given_vault()
        {
            KeyArgs args = new KeyArgs();
            args.Name = "Wallet Test";
            args.Description = "Test Key used for test purposes";
            args.Type = "asymmetric";
            args.Spec = "secp256k1";
            args.Usage = "sign/verify";

            Dictionary<string, object> parameters = new Dictionary<string,object>
            {
                {
                    "keyArgs", args
                }
            };
            var result = await _wallet.NewAccount(parameters);

            result.Should().NotBeNull();
        }

        [Test]
        public async Task can_create_a_default_account_within_a_given_vault()
        {
            KeyArgs args = null;
            Dictionary<string, object> parameters = new Dictionary<string,object>
            {
                {
                    "keyArgs", args
                }
            };
            var result = await _wallet.NewAccount(parameters);

            result.Should().NotBeNull();
        }

    
        [Test]
        public async Task can_delete_an_account()
        {
            KeyArgs args = null;
            Dictionary<string, object> parameters = new Dictionary<string,object>
            {
                {
                    "keyArgs", args
                }
            };
            var acc = await _wallet.NewAccount(parameters);

            await _wallet.DeleteAccount(acc);

            var accountId = await _wallet.GetKeyIdByAddress(acc);

            Assert.IsNull(accountId);
        }

        // [Test]
        // public async Task can_set_vault_id_from_configuration()
        // {
        //     _config.VaultId = "test-vaultId";
        //     var result = await _wallet.SetWalletVault();

        //     result.Should().NotBeNull();
        //     Assert.AreEqual(_config.VaultId, result);
        // }

        [Test]
        public async Task can_set_default_vault_id()
        {
            var result = await _wallet.SetWalletVault();
            result.Should().NotBeNull();
        }

        // [TestCase("0x3aa19B7Ee674f32F956F8cD25bFAacB416A2Feca")]
        // [TestCase("0x6Dc0d33830d48F4a07b76Bb8DB09f3ED76801E7D")]
        // public async Task can_sign_a_message_with_vault_key(string testAddress)
        // {
        //     Address address = new Address(testAddress);
        //     Keccak message = new Keccak("0x4d46fa23b8c33e29753e4738abd05148ffc8b346b34780b92435ad392325c45f");
        //     var result = await _wallet.Sign(address, message);
        //     Console.WriteLine(result);
        //     // result.Should().NotBeNull();
        //     // result.Should().BeOfType<Address>();
        // }
    }
}