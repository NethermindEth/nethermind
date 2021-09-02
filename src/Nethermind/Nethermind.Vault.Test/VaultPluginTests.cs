﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

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
            VaultConfig vaultConfig = new() {Enabled = enabled};
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
