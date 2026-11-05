// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.EthStats.Configs;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.EthStats.Test;

[NonParallelizable]
public class EthStatsPluginTests
{
    [Test]
    public void Enabled_reflects_config([Values] bool enabled)
    {
        EthStatsPlugin plugin = new(new EthStatsConfig { Enabled = enabled });

        Assert.That(plugin.Enabled, Is.EqualTo(enabled));
    }

    [Test]
    public void Module_registers_the_eth_stats_step()
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new EthStatsPlugin(new EthStatsConfig()).Module);
        using IContainer container = builder.Build();

        Assert.That(container.Resolve<IEnumerable<StepInfo>>(),
            Has.Some.Matches<StepInfo>(static s => s.StepType == typeof(EthStatsStep)));
    }

    [Test]
    public async Task Step_log_omits_server_credentials([Values] bool maskSensitiveData)
    {
        bool previous = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = maskSensitiveData;
        try
        {
            TestLogger logger = new();
            IEnode enode = Substitute.For<IEnode>();
            enode.Info.Returns("test-node");
            EthStatsStep step = new(
                Substitute.For<ISpecProvider>(), Substitute.For<ITxPool>(), Substitute.For<IBlockTree>(),
                Substitute.For<IPeerManager>(), Substitute.For<IGasPriceOracle>(), Substitute.For<IEthSyncingInfo>(),
                enode, new EthStatsConfig { Server = "ftp://user:password@example.org/secret?token=private" },
                Substitute.For<INetworkConfig>(), Substitute.For<IMiningConfig>(), new OneLoggerLogManager(new(logger)));

            try
            {
                await Assert.ThrowsAsync<System.ArgumentException>(() => step.Execute(CancellationToken.None));
            }
            finally
            {
                await step.DisposeAsync();
            }

            string line = logger.LogList.Single(log => log.StartsWith("Initializing ETH Stats"));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(line, Does.Contain(maskSensitiveData ? "[redacted]" : "ftp://example.org"));
                Assert.That(line, Does.Not.Contain("user:password").And.Not.Contain("/secret").And.Not.Contain("token=private"));
            }
        }
        finally
        {
            SensitiveLogMasking.Enabled = previous;
        }
    }
}
