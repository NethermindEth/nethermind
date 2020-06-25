using FluentAssertions;
using Nethermind.Core;
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
    public class VaultWalletTests
    {
        private IVaultConfig _config;
        private VaultWallet _wallet;

        [SetUp]
        public void SetUp()
        {
            _config = new VaultConfig();
            _config.Host = "vault.provide.services";
            _config.Scheme = "https";
            _config.Path = "api/v1";
            _config.Token = "bearer eyJhbGciOiJSUzI1NiIsImtpZCI6ImU2OmY3OmQ1OjI0OmUyOjU5OjA2OjJiOmJjOmEyOjhjOjM1OjlkOmNhOjBhOjg3IiwidHlwIjoiSldUIn0.eyJhdWQiOiJodHRwczovL2lkZW50LnByb3ZpZGUuc2VydmljZXMvYXBpL3YxIiwiaWF0IjoxNTkxNjI0ODE3LCJpc3MiOiJodHRwczovL2lkZW50LnByb3ZpZGUuc2VydmljZXMiLCJqdGkiOiI2NWM3YmVhMC0zZGQ3LTRlZWYtOGJkNi1lMDAxYTc1ZjMyNmEiLCJuYXRzIjp7InBlcm1pc3Npb25zIjp7InN1YnNjcmliZSI6eyJhbGxvdyI6WyJhcHBsaWNhdGlvbi4wMGYwMzMxZC04ODE5LTQ3YjQtOWNhMi1hNmY4M2MzN2NhMzEiLCJuZXR3b3JrLiouY29ubmVjdG9yLioiLCJuZXR3b3JrLiouc3RhdHVzIiwicGxhdGZvcm0uXHUwMDNlIl19fX0sInBydmQiOnsiYXBwbGljYXRpb25faWQiOiIwMGYwMzMxZC04ODE5LTQ3YjQtOWNhMi1hNmY4M2MzN2NhMzEiLCJleHRlbmRlZCI6eyJwZXJtaXNzaW9ucyI6eyIqIjo1MTB9fSwicGVybWlzc2lvbnMiOjUxMH0sInN1YiI6ImFwcGxpY2F0aW9uOjAwZjAzMzFkLTg4MTktNDdiNC05Y2EyLWE2ZjgzYzM3Y2EzMSJ9.xYtzf3xsa4N4p4rD2PZJbwJKrJl-jtYDgnanofvpmoTaEwIXeOX5s6OkSGBvoM-9Z9krm-ggkO4NoW8ub-5SLCAAiLYaTQi-sk7OnjHqV3r7JFZYfjdFtgPRziRPqDtZMpjeRPcNro5Hg5o9OK2uvhyg9TBk0mqFgm9i4AF-xtjfpIvoEFxCnfjqtsiLpoLVabEXrGHQ02X6Lbrq2e8bSkxAmarX5qH66wVPqjmV1JUDsNAh3ql9i8LROgzyitUWGih3fB_Rd2t6wzJOG2W4DMn6nDZJHSQhTpH0rAoz6h2f7opIVb-4RxGciPXzItPLJwGiJiTSfO-0Y6_SUAX1jWB2QD64Bz5wyxws9Qs-3scAjSnNE1zM7-Dcb3_CDRv8QCQuZU_maRSXm_KdN2YwmowgCthvMEwpbveM7scb8-k70PQU3Yg6OvnrzeSERBif5omATLdrumgmVnWqDuQI-LzFd40iy91WW6YifSqvaNX3I1oiBj3btR7veb6kG_5AMLKWqHwhKJF_yPtGNQfRl_e2JvZ0LyTvf0UzI9J7etqOGm__9E9bvNhkqgZkMBf1omt4QncCmEWY6KJIJq1cV72shVuEf6ZQp-aMdhfVURKzAARapy1Y_2PGY3FKPz9fL2hbRS9UHXTqqEll-ZAde0eASs9kVQ3s_VnRrAx5YQ0";
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

        [Test]
        public async Task can_set_vault_id_from_configuration()
        {
            _config.VaultId = "vaultId";
            var result = await _wallet.SetWalletVault();
\
            result.Should().NotBeNull();
            Assert.AreEqual(_config.VaultId, result);
        }

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