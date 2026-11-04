// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.FlatPriorityRead;

public class FlatPriorityReadModule(IFlatPriorityReadConfig config) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        // Without flat state no IPersistence is registered, so the decorator never applies.
        builder
            .AddDecorator<IPersistence, FlatPriorityReadPersistence>()
            .AddDecorator<IRocksDbConfigFactory, FlatPriorityReadRocksDbConfigFactory>();

        _ = config.Backend switch
        {
            FlatPriorityReadBackend.Memory => builder.AddSingleton<IPrioritySlotStore, MemoryPrioritySlotStore>(),
            FlatPriorityReadBackend.Lmdb => builder.AddSingleton<IPrioritySlotStore, LmdbPrioritySlotStore>(),
            FlatPriorityReadBackend.RocksDb => builder.AddSingleton<IPrioritySlotStore, RocksDbPrioritySlotStore>(),
            _ => throw new NotSupportedException($"Unsupported {nameof(IFlatPriorityReadConfig.Backend)} {config.Backend}"),
        };
    }
}
