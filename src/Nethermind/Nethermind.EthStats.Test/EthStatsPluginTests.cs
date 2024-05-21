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
    public IEthStatsConfig StatsConfig { get; private set; } = null!;
    private NethermindApi _context = null!;
#pragma warning disable NUnit1032
    private INethermindPlugin _plugin = null!;
#pragma warning restore NUnit1032

    [SetUp]
    public void Setup()
    {
        _context = Build.ContextWithMocks();
        _plugin = new EthStatsPlugin();
    }

    [TearDown]
    public void TearDown() => _plugin.DisposeAsync();

    [TestCase(true)]
    [TestCase(false)]
    public void Init_eth_stats_plugin_does_not_throw_exception(bool enabled)
    {
        StatsConfig = new EthStatsConfig() { Enabled = enabled };
        Assert.DoesNotThrow(() => _plugin.InitRlpDecoders(_context));
        Assert.DoesNotThrowAsync(async () => await _plugin.Init(_context));
        Assert.DoesNotThrowAsync(async () => await _plugin.InitNetworkProtocol());
        Assert.DoesNotThrowAsync(async () => await _plugin.InitRpcModules());
        Assert.DoesNotThrowAsync(async () => await _plugin.DisposeAsync());
    }
}
