// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.State.Flat.History.Prestate;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class PrestateRecordsModuleTests
{
    [TestCase(false, TestName = "PrestateRecords_WhenTheFlagIsOff_RegisterNoCacheAndNoDatabase")]
    [TestCase(true, TestName = "PrestateRecords_WhenTheFlagIsOn_RegisterTheCacheAndTheDatabase")]
    public void PrestateRecords_FollowTheFlag(bool enabled)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig
            {
                Enabled = true,
                HistoryEnabled = true,
                HistoryTransactionIndexEnabled = true,
                HistoryPrestateRecordsEnabled = enabled
            }))
            .Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.IsRegistered<PrestateRecordCache>(), Is.EqualTo(enabled), "a node without the flag must pay nothing");
            Assert.That(container.IsRegisteredWithKey<IDb>(DbNames.PrestateRecords), Is.EqualTo(enabled), "a node without the flag opens no prestateRecords database");
        }
    }

    [TestCase(true, false, TestName = "PrestateRecords_WithoutTheTransactionIndex_AreRefused")]
    [TestCase(false, false, TestName = "PrestateRecords_WithoutHistory_AreRefused")]
    public void PrestateRecords_WithoutWhatWritesThem_AreRefused(bool historyEnabled, bool indexEnabled)
    {
        ContainerBuilder builder = new();

        Assert.That(() => builder.AddModule(new TestNethermindModule(new FlatDbConfig
        {
            Enabled = true,
            HistoryEnabled = historyEnabled,
            HistoryTransactionIndexEnabled = indexEnabled,
            HistoryPrestateRecordsEnabled = true
        })).Build().Dispose(), Throws.InstanceOf<InvalidConfigurationException>(), "records nobody writes must not be silently accepted");
    }
}
