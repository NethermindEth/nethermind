// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.AuRa.Test;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeFinalizationManagerRegistrationTests
{
    [Test]
    public void Post_merge_head_skips_initial_finalization_scan()
    {
        // Post-merge blocks are never AuRa-finalized, so on a post-merge head the scan would read
        // every level back to the merge transition, or down to genesis on a post-merge synced node.
        CountingChainLevelInfoRepository chainLevelInfoRepository = new();
        using IContainer container = BuildContainer(headNumber: 200_000, headIsPostMerge: true, chainLevelInfoRepository);

        IAuRaBlockFinalizationManager finalizationManager = container.Resolve<IAuRaBlockFinalizationManager>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainLevelInfoRepository.LoadLevelCalls, Is.EqualTo(0UL));
            Assert.That(finalizationManager.LastFinalizedBlockLevel, Is.EqualTo(0UL));
        }
    }

    [Test]
    public void Pre_merge_head_still_loads_last_finalized_level_eagerly()
    {
        // Archive sync from genesis: MultiValidator picks the active validator from this value during
        // DI resolution, so it must be loaded before the branch processor is wired.
        CountingChainLevelInfoRepository chainLevelInfoRepository = new(finalizedLevel: 1_301);
        using IContainer container = BuildContainer(headNumber: 2_000, headIsPostMerge: false, chainLevelInfoRepository);

        IAuRaBlockFinalizationManager finalizationManager = container.Resolve<IAuRaBlockFinalizationManager>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finalizationManager.LastFinalizedBlockLevel, Is.EqualTo(1_301UL));
            Assert.That(chainLevelInfoRepository.LoadLevelCalls, Is.EqualTo(2_000UL - 1_301UL + 1UL));
        }
    }

    private static IContainer BuildContainer(ulong headNumber, bool headIsPostMerge, IChainLevelInfoRepository chainLevelInfoRepository)
    {
        ChainSpec chainSpec = new()
        {
            SealEngineType = SealEngineType.AuRa,
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.WithDifficulty(0).WithAura(0, new byte[65]).WithBlobGasUsed(0).TestObject,
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new AuRaChainSpecEngineParameters
                {
                    WithdrawalContractAddress = TestItem.AddressA,
                    StepDuration = { { 0, 3 } },
                    ValidatorsJson = new AuRaChainSpecEngineParameters.AuRaValidatorJson { List = [Address.Zero] }
                })
        };

        Block head = Build.A.Block.WithNumber(headNumber).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(head.Header).Returns(headIsPostMerge);

        return new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(), chainSpec))
            .AddModule(new AuRaModule(chainSpec))
            .AddModule(new AuRaMergeModule())
            .AddSingleton(blockTree)
            .AddSingleton(chainLevelInfoRepository)
            .AddSingleton(poSSwitcher)
            .Build();
    }
}
