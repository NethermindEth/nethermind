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
using Nethermind.Logging;
using Nethermind.Vault.Config;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;
using provide.Model.Vault;

namespace Nethermind.Vault.Test
{
    [TestFixture]
    public class VaultServiceTests
    {
        private IVaultConfig _config;
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
            _vaultService = new VaultService(_config, LimboLogs.Instance);
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
            var vaults = await _vaultService.ListVaultIds();
            foreach (Guid vault in vaults)
            {
                await _vaultService.DeleteVault(vault);
            }
        }

        [Test]
        public async Task can_return_a_list_of_vaults()
        {
            provide.Model.Vault.Vault vault = new provide.Model.Vault.Vault();
            vault.Name = "Wallet Vault Test";
            vault.Description = "Test Vault used for test purposes";

            provide.Model.Vault.Vault createdVault = await _vaultService.CreateVault(vault);
            createdVault.Id.Should().NotBeNull();

            IEnumerable<Guid> result = await _vaultService.ListVaultIds();
            result.Should().Contain(createdVault.Id!.Value);
        }


        [Test]
        public async Task can_create_a_new_vault()
        {
            provide.Model.Vault.Vault vault = new provide.Model.Vault.Vault();
            vault.Name = "Wallet Vault Test";
            vault.Description = "Test Vault used for test purposes";

            provide.Model.Vault.Vault result = await _vaultService.CreateVault(vault);
            result.Should().NotBeNull();
            result.Id.Should().NotBeNull();
        }


        [Test]
        public async Task can_delete_vault()
        {
            provide.Model.Vault.Vault vault = new provide.Model.Vault.Vault();
            vault.Name = "Wallet Vault Test";
            vault.Description = "Test Vault used for test purposes";

            provide.Model.Vault.Vault createdVault = await _vaultService.CreateVault(vault);
            createdVault.Id.Should().NotBeNull();

            await _vaultService.DeleteVault(createdVault.Id!.Value);
            IEnumerable<Guid> vaults = await _vaultService.ListVaultIds();
            vaults.Should().NotContain(createdVault.Id.Value);
        }

        [Test]
        public async Task can_delete_key()
        {
            provide.Model.Vault.Vault vault = new provide.Model.Vault.Vault();
            vault.Name = "Wallet Vault Test";
            vault.Description = "Test Vault used for test purposes";

            provide.Model.Vault.Vault createdVault = await _vaultService.CreateVault(vault);

            Key key = new Key();
            key.Name = "Test Key";
            key.Description = "Test Key used for test purposes";
            key.Type = "asymmetric";
            key.Spec = "secp256k1";
            key.Usage = "sign/verify";
            Key createdKey = await _vaultService.CreateKey(createdVault.Id.Value, key);
            await _vaultService.DeleteKey(createdVault.Id.Value, createdKey.Id.Value);
        }

        [Test]
        public async Task can_delete_key_via_listed()
        {
            provide.Model.Vault.Vault vault = new provide.Model.Vault.Vault();
            vault.Name = "Wallet Vault Test";
            vault.Description = "Test Vault used for test purposes";

            provide.Model.Vault.Vault createdVault = await _vaultService.CreateVault(vault);

            Key key = new Key();
            key.Name = "Test Key";
            key.Description = "Test Key used for test purposes";
            key.Type = "asymmetric";
            key.Spec = "secp256k1";
            key.Usage = "sign/verify";

            _ = await _vaultService.CreateKey(createdVault.Id.Value, key);
            var keys = await _vaultService.ListKeys(createdVault.Id.Value);
            foreach (var listedKey in keys)
            {
                await _vaultService.DeleteKey(createdVault.Id.Value, listedKey.Id.Value);
            }
        }
    }
}
