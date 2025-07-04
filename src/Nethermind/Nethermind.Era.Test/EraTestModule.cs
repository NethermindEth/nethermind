// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;

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
            .AddInstance<IBlockTree>(blockTreeBuilder.TestObject)
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
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddModule(new EraModule())
            .AddInstance<IBlockValidator>(Always.Valid)
            .AddInstance<IFileSystem>(new FileSystem()) // Run on real filesystem.
            .AddInstance<IEraConfig>(new EraConfig()
            {
                MaxEra1Size = 16,
                NetworkName = TestNetwork,
            })
            .AddKeyedSingleton<TempPath>("file", ctx => TempPath.GetTempFile())
            .AddKeyedSingleton<TempPath>("directory", ctx => TempPath.GetTempDirectory());
    }
}
