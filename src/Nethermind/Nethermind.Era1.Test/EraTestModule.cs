// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;

namespace Nethermind.Era1.Test;

public class EraTestModule(bool useRealValidator = false) : Module
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

    public static async Task<IContainer> CreateExportedEraEnvWithCompleteBlockBuilder(int chainLength = 512, int start = 0, int end = 0, CancellationToken cancellationToken = default)
    {
        IContainer testCtx = new ContainerBuilder()
            .AddModule(new EraTestModule(useRealValidator: true))
            .Build();

        await testCtx.Resolve<PseudoNethermindRunner>().StartBlockProcessing(cancellationToken);

        var util = testCtx.Resolve<TestBlockchainUtil>();
        for (int i = 0; i < chainLength - 1; i++)
        {
            await util.AddBlock(cancellationToken);
        }

        await testCtx.Resolve<IEraExporter>().Export(testCtx.ResolveTempDirPath(), start, end);
        return testCtx;
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
            .AddModule(TestNethermindModule.CreateWithRealChainSpec())
            .AddModule(new EraModule())
            .AddSingleton<IFileSystem>(new FileSystem()) // Run on real filesystem.
            .AddSingleton<IEraConfig>(new EraConfig()
            {
                MaxEra1Size = 16,
                NetworkName = TestNetwork,
            })
            .AddSingleton<ITimestamper>(ManualTimestamper.PreMerge)
            .AddKeyedSingleton<TempPath>("file", ctx => TempPath.GetTempFile())
            .AddKeyedSingleton<TempPath>("directory", ctx => TempPath.GetTempDirectory());

        if (!useRealValidator)
        {
            builder.AddSingleton<IBlockValidator>(Always.Valid);
        }
    }
}
