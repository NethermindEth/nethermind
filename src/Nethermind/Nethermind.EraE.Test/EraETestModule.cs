// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using System.IO.Abstractions;
using Nethermind.EraE.Config;
using Nethermind.EraE.Export;
using Testably.Abstractions;

namespace Nethermind.EraE.Test;

public class EraETestModule(bool useRealValidator = false) : Module
{
    public const string TestNetwork = "abc";

    public static ContainerBuilder BuildContainerBuilder()
    {
        return new ContainerBuilder().AddModule(new EraETestModule());
    }

    public static ContainerBuilder BuildContainerBuilderWithBlockTreeOfLength(int length)
    {
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree();

        return new ContainerBuilder()
            .AddModule(new EraETestModule())
            .AddSingleton<IBlockTree>(blockTreeBuilder.TestObject)
            .OnBuild(ctx =>
            {
                blockTreeBuilder
                    .WithTransactions(ctx.Resolve<IReceiptStorage>())
                    .OfChainLength(length);
            });
    }

    public static async Task<IContainer> CreateExportedEraEnv(int chainLength = 512, long from = 0, long to = 0)
    {
        IContainer testCtx = BuildContainerBuilderWithBlockTreeOfLength(chainLength).Build();
        await testCtx.Resolve<IEraExporter>().Export(testCtx.ResolveTempDirPath(), from, to);
        return testCtx;
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .AddModule(TestNethermindModule.CreateWithRealChainSpec())
            .AddModule(new EraEModule())
            .AddSingleton<IFileSystem>(new RealFileSystem())
            .AddSingleton<IEraEConfig>(new EraEConfig()
            {
                MaxEraSize = 16,
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
