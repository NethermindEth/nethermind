// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.State.Flat.History.Prestate;

namespace Nethermind.Init.Modules;

/// <summary>
/// Block pre-state records: written by the transaction changeset builder, read by historical states. Loaded only
/// when <see cref="IFlatDbConfig.HistoryPrestateRecordsEnabled"/> is set, so a node without it opens no database.
/// </summary>
public class PrestateRecordsModule : Module
{
    protected override void Load(ContainerBuilder builder) =>
        builder
            .AddDatabase(DbNames.PrestateRecords)
            .AddSingleton<PrestateRecordStore>(ctx => new PrestateRecordStore(ctx.ResolveKeyed<IDb>(DbNames.PrestateRecords)))
            .AddSingleton<PrestateRecordCache>()
            .AddSingleton<PrestateRecorder>();
}
