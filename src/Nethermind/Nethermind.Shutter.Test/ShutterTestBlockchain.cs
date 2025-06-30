// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Db.Blooms;
using Nethermind.Shutter.Config;
using static Nethermind.Merge.AuRa.Test.AuRaMergeEngineModuleTests;

namespace Nethermind.Shutter.Test;

public class ShutterTestBlockchain(Random rnd, ITimestamper? timestamper = null, ShutterEventSimulator? eventSimulator = null) : MergeAuRaTestBlockchain(null, null)
{
    public ShutterApiSimulator Api => Container.Resolve<ShutterApiSimulator>();
    protected readonly Random _rnd = rnd;
    protected readonly ITimestamper? _timestamper = timestamper;

    protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider)
    {
        base.ConfigureContainer(builder, configProvider)
            .AddModule(new ShutterPluginModule())
            .AddSingleton<ShutterApiSimulator>()
            .AddSingleton<ShutterEventSimulator>((ctx) => ShutterTestsCommon.InitEventSimulator(_rnd))
            .AddSingleton<ShutterValidatorsInfo>(new ShutterValidatorsInfo())
            .AddSingleton<Random>(_rnd)
            .AddSingleton<IShutterConfig>(ShutterTestsCommon.Cfg)
            .Bind<ShutterApi, ShutterApiSimulator>()

            // ShutterApiSimulator add receipts to block with empty transaction. Crash with full receipt storage.
            .AddSingleton<IReceiptStorage, InMemoryReceiptStorage>()

            // It seems that it does not work with bloom.
            // This or use a separate bloom storage for LogFinder and BlockTree.
            .AddSingleton<IBloomStorage>(NullBloomStorage.Instance)
            ;

        if (eventSimulator is not null)
        {
            builder.AddSingleton<ShutterEventSimulator>(eventSimulator);
        }

        if (_timestamper is not null)
        {
            builder.AddSingleton<ITimestamper>(_timestamper);
        }

        return builder;
    }
}
