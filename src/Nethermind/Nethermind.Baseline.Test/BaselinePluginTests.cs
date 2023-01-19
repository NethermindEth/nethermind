// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Baseline.Config;
using Nethermind.Db;
using Nethermind.Plugin.Baseline;
using NUnit.Framework;
using Nethermind.Runner.Test.Ethereum;
using NSubstitute;

namespace Nethermind.Baseline.Test
{
    public class BaselinePluginTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void Init_baseline_plugin_does_not_throw_exception(bool enabled)
        {
            BaselineConfig baselineConfig = new() { Enabled = enabled };
            NethermindApi context = Build.ContextWithMocks();
            context.ConfigProvider.GetConfig<IBaselineConfig>().Returns(baselineConfig);
            context.MemDbFactory = new MemDbFactory();
            BaselinePlugin plugin = new();
            Assert.DoesNotThrowAsync(async () => { await plugin.Init(context); });
            Assert.DoesNotThrow(() => { plugin.InitBlockchain(); });
            Assert.DoesNotThrow(() => { plugin.InitNetworkProtocol(); });
            Assert.DoesNotThrowAsync(async () => { await plugin.InitRpcModules(); });
            Assert.DoesNotThrowAsync(async () => { await plugin.DisposeAsync(); });
        }
    }
}
