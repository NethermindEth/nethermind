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

    // Beacon genesis: 1606824023s (1 Dec 2020). Post-merge blocks must have timestamps after this.
    // The Merge was ~15 Sep 2022 (1663224162s). We use a round timestamp well past both.
    private const ulong PostMergeGenesisTimestamp = 1_663_000_000;

    public static ContainerBuilder BuildContainerBuilderWithPostMergeBlockTreeOfLength(int length)
    {
        // Set genesis timestamp past beacon-chain genesis so SlotTime.GetSlot succeeds on all blocks.
        Block genesis = Build.A.Block.Genesis.WithTimestamp(PostMergeGenesisTimestamp).WithPostMergeRules().TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).WithPostMergeRules();

        return new ContainerBuilder()
            .AddModule(new EraETestModule())
            .AddSingleton<ITimestamper>(PostMerge)
            .AddSingleton<IBlockTree>(blockTreeBuilder.TestObject)
            .OnBuild(ctx =>
            {
                blockTreeBuilder
                    .WithTransactions(ctx.Resolve<IReceiptStorage>())
                    .OfChainLength(length);

                // Post-merge blocks have TotalDifficulty=0, which never satisfies
                // HeadImprovementRequirementsSatisfied (0 < MainnetTTD). Force-update head
                // so the exporter can resolve blockTree.Head correctly.
                Block? lastBlock = blockTreeBuilder.BlockTree.FindBlock(length - 1, BlockTreeLookupOptions.None);
                if (lastBlock is not null)
                    blockTreeBuilder.BlockTree.UpdateMainChain(new[] { lastBlock }, true, forceUpdateHeadBlock: true);
            });
    }

    // Matches PostMergeGenesisTimestamp: 1663000000s = 2022-09-12 18:26:40 UTC
    public static ManualTimestamper PostMerge =>
        new(DateTimeOffset.FromUnixTimeSeconds((long)PostMergeGenesisTimestamp).UtcDateTime);

    public static async Task<IContainer> CreateExportedEraEnv(int chainLength = 512, long from = 0, long to = 0)
    {
        IContainer testCtx = BuildContainerBuilderWithBlockTreeOfLength(chainLength).Build();
        await testCtx.Resolve<IEraExporter>().Export(testCtx.ResolveTempDirPath(), from, to);
        return testCtx;
    }

    public static async Task<IContainer> CreateExportedPostMergeEraEnv(int chainLength = 16)
    {
        IContainer testCtx = BuildContainerBuilderWithPostMergeBlockTreeOfLength(chainLength).Build();
        // Start from block 1: genesis is pre-merge in all block trees.
        await testCtx.Resolve<IEraExporter>().Export(testCtx.ResolveTempDirPath(), from: 1, to: 0);
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
