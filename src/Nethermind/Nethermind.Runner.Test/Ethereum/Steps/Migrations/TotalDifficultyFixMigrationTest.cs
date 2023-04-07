// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        Dictionary<Keccak, BlockHeader> hashesToHeaders = new();
        headers[0] = Core.Test.Builders.Build.A.BlockHeader.WithDifficulty(1).TestObject;
        for (int i = 1; i < numberOfBlocks; ++i)
        {
            headers[i] = Core.Test.Builders.Build.A.BlockHeader.WithParent(headers[i - 1]).WithDifficulty((UInt256)i + 1).TestObject;
            hashesToHeaders.Add(headers[i].Hash!, headers[i]);
        }

        // Setup db
        ChainLevelInfo[] levels = new ChainLevelInfo[numberOfBlocks];
        for (int i = 0; i < numberOfBlocks; ++i)
        {
            levels[i] = new ChainLevelInfo(true, new BlockInfo(headers[i].Hash!, (UInt256)((i + 1) * (i + 2)) / 2));
        }

        ChainLevelInfo[] persistedLevels = new ChainLevelInfo[numberOfBlocks];

        // Setup api
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Keccak>()).Returns(info => hashesToHeaders[(Keccak)info[0]]);
        blockTree.BestKnownNumber.Returns(numberOfBlocks);

        IChainLevelInfoRepository chainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();
        chainLevelInfoRepository.LoadLevel(Arg.Any<long>()).Returns(info => levels[(long)info[0]]);
        chainLevelInfoRepository
            .When(x => x.PersistLevel(Arg.Any<long>(), Arg.Any<ChainLevelInfo>()))
            .Do(x => persistedLevels[(long)x[0]] = (ChainLevelInfo)x[1]);

        SyncConfig syncConfig = new()
        {
            FixTotalDifficulty = true,
            FixTotalDifficultyStartingBlock = firstBlock,
            FixTotalDifficultyLastBlock = lastBlock
        };
        TotalDifficultyFixMigration migration = new(chainLevelInfoRepository, blockTree, syncConfig, new TestLogManager());

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

    [Test]
    public void should_fix_non_canonical()
    {
        Dictionary<Keccak, BlockHeader> hashesToHeaders = new();
        BlockHeader g = Core.Test.Builders.Build.A.BlockHeader.WithDifficulty(1).TestObject;

        // Canonical
        BlockHeader c1 = Core.Test.Builders.Build.A.BlockHeader.WithParent(g).WithDifficulty(2).TestObject;
        BlockHeader c2 = Core.Test.Builders.Build.A.BlockHeader.WithParent(c1).WithDifficulty(3).TestObject;
        BlockHeader c3 = Core.Test.Builders.Build.A.BlockHeader.WithParent(c2).WithDifficulty(4).TestObject;
        BlockHeader c4 = Core.Test.Builders.Build.A.BlockHeader.WithParent(c3).WithDifficulty(5).TestObject;
        // Non canonical
        BlockHeader nc2 = Core.Test.Builders.Build.A.BlockHeader.WithParent(c1).WithDifficulty(100).TestObject;
        BlockHeader nc3 = Core.Test.Builders.Build.A.BlockHeader.WithParent(nc2).WithDifficulty(200).TestObject;

        // g - c1 - c2 - c3 - c4
        //        \ nc2 - nc3

        ChainLevelInfo[] levels = new ChainLevelInfo[5];
        levels[0] = new ChainLevelInfo(true, new BlockInfo(g.Hash!, 1));
        levels[1] = new ChainLevelInfo(true, new BlockInfo(c1.Hash!, 3));
        levels[2] = new ChainLevelInfo(true, new BlockInfo(c2.Hash!, 6), new BlockInfo(nc2.Hash!, 103));
        levels[3] = new ChainLevelInfo(true, new BlockInfo(c3.Hash!, 10), new BlockInfo(nc3.Hash!, 303));
        levels[4] = new ChainLevelInfo(true, new BlockInfo(c4.Hash!, 15));

        // Break c4 and nc3
        levels[4].BlockInfos[0].TotalDifficulty = 999;
        levels[3].BlockInfos[1].TotalDifficulty = 888;

        hashesToHeaders[g.Hash] = g;
        hashesToHeaders[c1.Hash] = c1;
        hashesToHeaders[c2.Hash] = c2;
        hashesToHeaders[c3.Hash] = c3;
        hashesToHeaders[c4.Hash] = c4;
        hashesToHeaders[nc2.Hash] = nc2;
        hashesToHeaders[nc3.Hash] = nc3;

        // Setup mocks
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Keccak>()).Returns(info => hashesToHeaders[(Keccak)info[0]]);
        blockTree.BestKnownNumber.Returns(5);

        ChainLevelInfo[] persistedLevels = new ChainLevelInfo[5];
        IChainLevelInfoRepository chainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();
        chainLevelInfoRepository.LoadLevel(Arg.Any<long>()).Returns(info => levels[(long)info[0]]);
        chainLevelInfoRepository
            .When(x => x.PersistLevel(Arg.Any<long>(), Arg.Any<ChainLevelInfo>()))
            .Do(x => persistedLevels[(long)x[0]] = (ChainLevelInfo)x[1]);

        SyncConfig syncConfig = new()
        {
            FixTotalDifficulty = true,
            FixTotalDifficultyStartingBlock = 1,
            FixTotalDifficultyLastBlock = 4
        };
        TotalDifficultyFixMigration migration = new(chainLevelInfoRepository, blockTree, syncConfig, new TestLogManager());

        // Run
        migration.Run();
        Thread.Sleep(3000);

        persistedLevels[0].Should().BeNull();
        persistedLevels[1].Should().BeNull();
        persistedLevels[2].Should().BeNull();

        persistedLevels[3].BlockInfos.Length.Should().Be(2);
        persistedLevels[3].BlockInfos[0].TotalDifficulty.Should().Be(10);
        persistedLevels[3].BlockInfos[1].TotalDifficulty.Should().Be(303);

        persistedLevels[4].BlockInfos.Length.Should().Be(1);
        persistedLevels[4].BlockInfos[0].TotalDifficulty.Should().Be(15);
    }
}
