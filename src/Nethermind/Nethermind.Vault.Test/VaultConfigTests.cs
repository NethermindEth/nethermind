// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
