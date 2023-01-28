// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Db;
using Nethermind.Plugin.Baseline;
using Nethermind.Vault.Config;
using NSubstitute;
using NUnit.Framework;
using Build = Nethermind.Runner.Test.Ethereum.Build;

namespace Nethermind.Vault.Test
{
    public class VaultPluginTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void Init_vault_plugin_does_not_throw_exception(bool enabled)
        {
            VaultConfig vaultConfig = new() { Enabled = enabled };
            NethermindApi context = Build.ContextWithMocks();
            context.ConfigProvider.GetConfig<VaultConfig>().Returns(vaultConfig);
            context.MemDbFactory = new MemDbFactory();
            VaultPlugin plugin = new();
            Assert.DoesNotThrowAsync(async () => { await plugin.Init(context); });
            Assert.DoesNotThrow(() => { plugin.InitBlockchain(); });
            Assert.DoesNotThrow(() => { plugin.InitNetworkProtocol(); });
            Assert.DoesNotThrowAsync(async () => { await plugin.InitRpcModules(); });
            Assert.DoesNotThrowAsync(async () => { await plugin.DisposeAsync(); });
        }
    }
}
