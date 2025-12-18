// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.EthStats.Configs;
using Nethermind.Runner.Test.Ethereum;
using NUnit.Framework;

namespace Nethermind.EthStats.Test;

public class EthStatsPluginTests
{
    private NethermindApi _context = null!;
#pragma warning disable NUnit1032
    private INethermindPlugin _plugin = null!;
#pragma warning restore NUnit1032

    [SetUp]
    public void Setup()
    {
        _context = Build.ContextWithMocks();
        _plugin = new EthStatsPlugin(new EthStatsConfig() { Enabled = true });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Init_eth_stats_plugin_does_not_throw_exception(bool enabled)
    {
        Assert.DoesNotThrow(() => _plugin.InitTxTypesAndRlpDecoders(_context));
        Assert.DoesNotThrowAsync(async () => await _plugin.Init(_context));
        Assert.DoesNotThrowAsync(async () => await _plugin.InitNetworkProtocol());
        Assert.DoesNotThrowAsync(async () => await _plugin.InitRpcModules());
    }
}
