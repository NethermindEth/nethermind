// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.Era1.Test;

public class EraTestModule : Module
{
    public const string TestNetwork = "abc";

    public static ContainerBuilder BuildContainerBuilder()
    {
        return new ContainerBuilder().AddModule(new EraTestModule());
    }

    public static ContainerBuilder BuildContainerBuilderWithBlockTreeOfLength(int length)
    {
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree();

        return new ContainerBuilder()
            .AddModule(new EraTestModule())
            .AddSingleton<IBlockTree>(blockTreeBuilder.TestObject)
            .OnBuild((ctx) =>
            {
                blockTreeBuilder
                    .WithTransactions(ctx.Resolve<IReceiptStorage>())
                    .OfChainLength(length);
            });
    }

    public static async Task<IContainer> CreateExportedEraEnv(int chainLength = 512, int start = 0, int end = 0)
    {
        IContainer testCtx = BuildContainerBuilderWithBlockTreeOfLength(chainLength).Build();
        await testCtx.Resolve<IEraExporter>().Export(testCtx.ResolveTempDirPath(), start, end);
        return testCtx;
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new EraModule())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<ISpecProvider>(Substitute.For<ISpecProvider>())
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<IProcessExitSource>(Substitute.For<IProcessExitSource>())
            .AddSingleton<ISyncConfig>(new SyncConfig()
            {
            })
            .AddSingleton<IFileSystem>(new FileSystem())
            .AddSingleton<IEraConfig>(new EraConfig()
            {
                MaxEra1Size = 16,
                NetworkName = TestNetwork,
            })
            .AddSingleton<IBlockTree>(Build.A.BlockTree().TestObject)

            // Need to be real because during import the receipts does not have txhash but the ensure canonical
            // assumed that the txhash is not null which would have been populated by receipt recovery but InMemoryReceiptStorage
            // does not do receipt recovery.
            .AddModule(new DbModule())
            .AddSingleton<IReceiptConfig>(new ReceiptConfig())
            .AddSingleton<IDbProvider>(TestMemDbProvider.Init())
            .AddSingleton<IBlockStore, BlockStore>()
            .AddSingleton<IEthereumEcdsa, EthereumEcdsa>()
            .AddSingleton<IReceiptsRecovery, ReceiptsRecovery>()
            .AddSingleton<IReceiptStorage, PersistentReceiptStorage>();


        builder
            .Register(ctx => TempPath.GetTempFile())
            .SingleInstance()
            .Named<TempPath>("file");

        builder
            .Register(ctx => TempPath.GetTempDirectory())
            .SingleInstance()
            .Named<TempPath>("directory");
    }
}
