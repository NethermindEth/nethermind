// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps.Migrations;

public class TotalDifficultyFixMigrationTest
{
    [TestCase(null, 4, 15)]
    [TestCase(5, 4, 15)]
    [TestCase(9, 9, 55)]
    [TestCase(3, 3, 10)]
    [TestCase(3, 4, -1)]
    [TestCase(4, 1, -1)]
    public void Should_fix_td_when_broken(long? lastBlock, long brokenLevel, long expectedTd)
    {
        long numberOfBlocks = 10;
        long firstBlock = 3;
        // Setup headers
        BlockHeader[] headers = new BlockHeader[numberOfBlocks];
        headers[0] = Core.Test.Builders.Build.A.BlockHeader.WithDifficulty(1).TestObject;
        for (int i = 1; i < numberOfBlocks; ++i)
        {
            headers[i] = Core.Test.Builders.Build.A.BlockHeader.WithParent(headers[i - 1]).WithDifficulty((UInt256)i + 1).TestObject;
        }

        // Setup db
        ChainLevelInfo[] levels = new ChainLevelInfo[numberOfBlocks];
        for (int i = 0; i < numberOfBlocks; ++i)
        {
            levels[i] = new ChainLevelInfo(true, new BlockInfo(headers[i].Hash!, (UInt256)((i + 1) * (i + 2)) / 2));
        }

        ChainLevelInfo[] persistedLevels = new ChainLevelInfo[numberOfBlocks];

        // Setup api
        NethermindApi api = new();
        api.BlockTree = Substitute.For<IBlockTree>();
        api.BlockTree.FindHeader(Arg.Any<long>()).Returns(info => headers[(long)info[0]]);
        api.BlockTree.BestKnownNumber.Returns(numberOfBlocks);

        api.ChainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();
        api.ChainLevelInfoRepository.LoadLevel(Arg.Any<long>()).Returns(info => levels[(long)info[0]]);
        api.ChainLevelInfoRepository
            .When(x => x.PersistLevel(Arg.Any<long>(), Arg.Any<ChainLevelInfo>()))
            .Do(x => persistedLevels[(long)x[0]] = (ChainLevelInfo)x[1]);
        api.LogManager = new TestLogManager();

        SyncConfig syncConfig = new()
        {
            FixTotalDifficulty = true,
            FixTotalDifficultyStartingBlock = firstBlock,
            FixTotalDifficultyLastBlock = lastBlock
        };
        TotalDifficultyFixMigration migration = new(api, syncConfig);

        // Break level
        levels[brokenLevel].BlockInfos[0].TotalDifficulty = 9999;

        // Run
        migration.Run();
        Thread.Sleep(300);

        // Check level fixed
        for (long i = 0; i < numberOfBlocks; ++i)
        {
            if (i == brokenLevel && firstBlock <= brokenLevel && brokenLevel <= (lastBlock ?? numberOfBlocks))
            {
                persistedLevels[i].BlockInfos[0].TotalDifficulty.Should().Be((UInt256)expectedTd);
            }
            else
            {
                persistedLevels[i].Should().Be(null);
            }
        }
    }
}
