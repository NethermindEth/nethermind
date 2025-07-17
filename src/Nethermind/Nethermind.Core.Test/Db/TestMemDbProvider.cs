// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Init.Modules;

namespace Nethermind.Core.Test.Db
{
    public class TestMemDbProvider
    {
        public static Task<IDbProvider> InitAsync()
        {
            return Task.FromResult(new ContainerBuilder()
                .AddModule(new DbModule(new InitConfig() { DiagnosticMode = DiagnosticMode.MemDb }, new ReceiptConfig(), new SyncConfig()))
                .AddSingleton<IDbProvider, ContainerOwningDbProvider>()
                .Build()
                .Resolve<IDbProvider>());
        }

        public static IDbProvider Init()
        {
            return new ContainerBuilder()
                .AddModule(new DbModule(new InitConfig() { DiagnosticMode = DiagnosticMode.MemDb }, new ReceiptConfig(), new SyncConfig()))
                .AddSingleton<IDbProvider, ContainerOwningDbProvider>()
                .Build()
                .Resolve<IDbProvider>();
        }
    }

    /// <summary>
    /// Like <see cref="DbProvider"/>, but also dispose lifetime scope. Useful for existing test to make sure
    /// container is disposed properly.
    /// </summary>
    public class ContainerOwningDbProvider(ILifetimeScope ctx) : DbProvider(ctx), IDisposable
    {
        public override void Dispose()
        {
            ctx.Dispose();
        }
    }
}
