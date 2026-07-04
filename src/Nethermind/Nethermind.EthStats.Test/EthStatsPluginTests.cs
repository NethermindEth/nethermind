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
    private INethermindPlugin _plugin = null!;

    [SetUp]
    public void Setup() => _context = Build.ContextWithMocks();

    public void Init_eth_stats_plugin_does_not_throw_exception([Values] bool enabled)
    {
        _plugin = new EthStatsPlugin(new EthStatsConfig() { Enabled = enabled });

        Assert.DoesNotThrow(() => _plugin.InitTxTypesAndRlpDecoders(_context));
        Assert.DoesNotThrowAsync(async () => await _plugin.Init(_context));
        Assert.DoesNotThrowAsync(async () => await _plugin.InitRpcModules());
    }
}
