//  Copyright (c) 2018 Demerzel Solutions Limited
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

using FluentAssertions;
using Nethermind.Vault.Config;
using Nethermind.Config;
using NUnit.Framework;
using System.Reflection;

namespace Nethermind.Vault.Test
{
    [TestFixture]
    public class VaultConfigTests
    {
        [Test]
        public void can_set()
        {
            var host = "localhost:8080";
            var scheme = "http";
            var path = "api/v2";
            var token = "12345";
            var vaultId = "vaultId";
            var vaultKeyFile = "vault_key";
            VaultConfig config = new VaultConfig();
            config.Enabled.Should().BeFalse();
            config.Enabled = true;
            config.Enabled.Should().BeTrue();
            config.Enabled = false;
            config.Enabled.Should().BeFalse();
            config.Host = host;
            config.Host.Should().Be(host);
            config.Scheme = scheme;
            config.Scheme.Should().Be(scheme);
            config.Path = path;
            config.Path.Should().Be(path);
            config.Token = token;
            config.Token.Should().Be(token);
            config.VaultId = vaultId;
            config.VaultId.Should().Be(vaultId);
            config.VaultKeyFile = vaultKeyFile;
            config.VaultKeyFile.Should().Be(vaultKeyFile);
        }
        [Test]
        public void defaults_are_fine()
        {
            VaultConfig config = new VaultConfig();
            var host = ((ConfigItemAttribute)(typeof(IVaultConfig).GetProperty("Host").GetCustomAttribute(typeof(ConfigItemAttribute)))).DefaultValue;
            var scheme = ((ConfigItemAttribute)(typeof(IVaultConfig).GetProperty("Scheme").GetCustomAttribute(typeof(ConfigItemAttribute)))).DefaultValue;
            var path = ((ConfigItemAttribute)(typeof(IVaultConfig).GetProperty("Path").GetCustomAttribute(typeof(ConfigItemAttribute)))).DefaultValue;
            var token = ((ConfigItemAttribute)(typeof(IVaultConfig).GetProperty("Token").GetCustomAttribute(typeof(ConfigItemAttribute)))).DefaultValue;
            var vaultId = ((ConfigItemAttribute)(typeof(IVaultConfig).GetProperty("VaultId").GetCustomAttribute(typeof(ConfigItemAttribute)))).DefaultValue;
            config.Host = "vault.provide.services";
            config.Scheme = "https";
            config.Path = "api/v1";
            config.Host.Should().Be(host);
            config.Scheme.Should().Be(scheme);
            config.Path.Should().Be(path);
            config.Token.Should().BeNull();
            config.VaultId.Should().BeNull();
        }
    }
}
