using FluentAssertions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.Styles;
using NSubstitute;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Nethermind.Vault.Test
{
    [TestFixture]
    public class VaultWalletTests
    {
        private IVaultConfig _config;
        private VaultWallet _wallet;

        [SetUp]
        public void SetUp()
        {
            _config = new VaultConfig();
            _config.Host = "localhost";
            _config.Scheme = "http";
            _config.Path = "api/v1";
            _config.Token = "12345";
            _config.VaultId = "vaultId";
            _wallet = new VaultWallet(
                Substitute.For<IVaultManager>(),
                _config,
                LimboLogs.Instance
            );

        }

        [TearDown]
        public async Task TearDown()
        {
            var accounts = await _wallet.GetAccounts();
            foreach (var acc in accounts)
            {
                await _wallet.DeleteAccount(acc);
            }      
        }

        [Test]
        public async Task can_return_a_list_of_accounts_for_a_given_vault()
        {
            KeyArgs args = null;
            var acc = await _wallet.NewAccount(args);

            var result = await _wallet.GetAccounts();

            result.Should().NotBeNullOrEmpty();
            result.Should().AllBeOfType<Address>();
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
            var result = await _wallet.NewAccount(args);

            result.Should().NotBeNull();
            result.Should().BeOfType<Address>();
        }

        [Test]
        public async Task can_create_a_default_account_within_a_given_vault()
        {
            KeyArgs args = null;
            var result = await _wallet.NewAccount(args);

            result.Should().NotBeNull();
            result.Should().BeOfType<Address>();
        }

    
        [Test]
        public async Task can_delete_an_account()
        {
            KeyArgs args = null;
            var acc = await _wallet.NewAccount(args);

            await _wallet.DeleteAccount(acc);

            var accountId = await _wallet.GetKeyIdByAddress(acc);

            Assert.IsNull(accountId);
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