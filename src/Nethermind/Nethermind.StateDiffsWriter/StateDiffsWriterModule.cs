// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Init.Modules;
using Nethermind.StateDiffsWriter.Service;
using Nethermind.StateDiffsWriter.Storage;

namespace Nethermind.StateDiffsWriter;

public class StateDiffsWriterModule : Module
{
    /// <summary>Name of the RocksDB column-family database for block diffs.</summary>
    public const string DbName = "blockDiffs";

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.AddColumnDatabase<BlockDiffsColumns>(DbName);

        builder.AddSingleton<BlockDiffsStore>();
        builder.AddSingleton<DiffsWriterService>();
        builder.AddSingleton<DiffsPruner>();

        builder.AddStep(typeof(InitializeStateDiffsWriterPlugin));
    }
}
