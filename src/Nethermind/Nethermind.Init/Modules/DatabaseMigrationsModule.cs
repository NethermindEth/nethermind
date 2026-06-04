// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Init.Steps.Migrations;

namespace Nethermind.Init.Modules;

public class DatabaseMigrationsModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<IDatabaseMigration, BloomMigration>()
        .AddSingleton<IDatabaseMigration, ReceiptMigration>()
        .Bind<IReceiptsMigration, ReceiptMigration>()
        .AddSingleton<IDatabaseMigration, ReceiptFixMigration>()
        .AddSingleton<IDatabaseMigration, TotalDifficultyFixMigration>();
}
