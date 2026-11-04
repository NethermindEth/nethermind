// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db.Rocks.Config;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.FlatPriorityRead;

public class FlatPriorityReadModule(IFlatPriorityReadConfig config) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        if (config.Backend != FlatPriorityReadBackend.Memory && !OperatingSystem.IsLinux())
        {
            throw new InvalidConfigurationException(
                $"FlatPriorityRead.Backend {config.Backend} keeps its files in /dev/shm, which only Linux has. Use the Memory backend instead.",
                ExitCodes.GeneralError);
        }

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
