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
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using NUnit.Framework;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Vault.Test
{
    [TestFixture]
    public class VaultWalletTests
    {
        private VaultWallet _wallet;
        private VaultService _vaultService;
        private Guid _vaultId;
        private VaultConfig _config;

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
            _vaultService = new VaultService(
                _config,
                new TestLogManager(LogLevel.Trace)
            );

            provide.Model.Vault.Vault vault = new provide.Model.Vault.Vault();
            vault.Name = "Name";
            vault.Description = "Description";
            
            // Create a single Vault instance
            provide.Model.Vault.Vault response = await _vaultService.CreateVault(vault);
            response.Id.Should().NotBeNull();

            _vaultId = response.Id!.Value;
            _wallet = new VaultWallet(_vaultService, _vaultId.ToString(), LimboLogs.Instance);
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
            var accounts = await _wallet.GetAccounts();
            foreach (Address acc in accounts)
            {
                await _wallet.DeleteAccount(acc);
            }

            await _vaultService.DeleteVault(_vaultId);
        }

        [Test]
        public async Task can_return_a_list_of_accounts_for_a_given_vault()
        {
            Address acc = await _wallet.CreateAccount();
            var result = await _wallet.GetAccounts();
            result.Should().NotBeNullOrEmpty();
            result.Should().Contain(acc);
        }

        [Test]
        public async Task can_create_a_new_account_within_a_given_vault()
        {
            Address result = await _wallet.CreateAccount();
            result.Should().NotBeNull();
        }

        [Test]
        public async Task can_delete_an_account()
        {
            Address acc = await _wallet.CreateAccount();
            await _wallet.DeleteAccount(acc);
            Guid? accountId = await _wallet.RetrieveId(acc);
            accountId.Should().BeNull();
        }

        [Test]
        public async Task can_sign_a_message_with_vault_key()
        {
            Address acc = await _wallet.CreateAccount();
            Keccak message = new Keccak("0x4d46fa23b8c33e29753e4738abd05148ffc8b346b34780b92435ad392325c45f");
            Signature result = await _wallet.Sign(acc, message);
            result.Should().NotBeNull();
        }

        [Test]
        public async Task can_verify_a_message_with_vault_key()
        {
            Address acc = await _wallet.CreateAccount();
            Keccak message = new Keccak("0x4d46fa23b8c33e29753e4738abd05148ffc8b346b34780b92435ad392325c45f");
            Signature signature = await _wallet.Sign(acc, message);
            bool result = await _wallet.Verify(acc, message, signature);
            result.Should().BeTrue();
        }
    }
}
