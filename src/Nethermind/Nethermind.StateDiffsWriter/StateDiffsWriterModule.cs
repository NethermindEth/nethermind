// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Init.Modules;
using Nethermind.StateDiffsWriter.Service;
using Nethermind.StateDiffsWriter.Storage;

namespace Nethermind.StateDiffsWriter;

public class StateDiffsWriterModule : Module
{
    /// <summary>
    /// Name of the new RocksDB column-family database. Resolves to
    /// <c>&lt;BaseDbPath&gt;/blockDiffs</c> when not overridden, with two CFs:
    /// <see cref="BlockDiffsColumns.Default"/> for per-block RLP records and
    /// <see cref="BlockDiffsColumns.SlotCounts"/> for the per-address running
    /// slot count map.
    /// </summary>
    public const string DbName = "blockDiffs";

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.AddColumnDatabase<BlockDiffsColumns>(DbName);

        builder.AddSingleton<BlockDiffsStore>();
        builder.AddSingleton<DiffsWriterService>();
        builder.AddSingleton<DiffsPruner>();
    }
}
