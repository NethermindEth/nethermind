// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    [TestCase(null, 4UL, 15UL)]
    [TestCase(5UL, 4UL, 15UL)]
    [TestCase(9UL, 9UL, 55UL)]
    [TestCase(3UL, 3UL, 10UL)]
    [TestCase(3UL, 4UL, ulong.MaxValue)]
    [TestCase(4UL, 1UL, ulong.MaxValue)]
    public async Task Should_fix_td_when_broken(ulong? lastBlock, ulong brokenLevel, ulong expectedTd)
    {
        ulong numberOfBlocks = 10;
        ulong firstBlock = 3;
        // Setup headers
        BlockHeader[] headers = new BlockHeader[numberOfBlocks];
        Dictionary<Hash256, BlockHeader> hashesToHeaders = [];
        headers[0] = Core.Test.Builders.Build.A.BlockHeader.WithDifficulty(1).TestObject;
        for (ulong i = 1; i < numberOfBlocks; ++i)
        {
            headers[i] = Core.Test.Builders.Build.A.BlockHeader.WithParent(headers[i - 1]).WithDifficulty((UInt256)i + 1).TestObject;
            hashesToHeaders.Add(headers[i].Hash!, headers[i]);
        }

        // Setup db
        ChainLevelInfo[] levels = new ChainLevelInfo[numberOfBlocks];
        for (ulong i = 0; i < numberOfBlocks; ++i)
        {
            levels[i] = new ChainLevelInfo(true, new BlockInfo(headers[i].Hash!, (UInt256)((i + 1) * (i + 2)) / 2));
        }

        ChainLevelInfo[] persistedLevels = new ChainLevelInfo[numberOfBlocks];

        // Setup api
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(info => hashesToHeaders[(Hash256)info[0]]);
        blockTree.BestKnownNumber.Returns(numberOfBlocks);

        IChainLevelInfoRepository chainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();
        chainLevelInfoRepository.LoadLevel(Arg.Any<ulong>()).Returns(info => levels[(ulong)info[0]]);
        chainLevelInfoRepository
            .When(x => x.PersistLevel(Arg.Any<ulong>(), Arg.Any<ChainLevelInfo>()))
            .Do(x => persistedLevels[(ulong)x[0]] = (ChainLevelInfo)x[1]);

        SyncConfig syncConfig = new()
        {
            FixTotalDifficulty = true,
            FixTotalDifficultyStartingBlock = firstBlock,
            FixTotalDifficultyLastBlock = lastBlock
        };
        TotalDifficultyFixMigration migration = new(chainLevelInfoRepository, blockTree, syncConfig, new TestLogManager());

        if (brokenLevel < numberOfBlocks)
        {
            levels[brokenLevel].BlockInfos[0].TotalDifficulty = 9999;
        }

        // Run
        await migration.Run(CancellationToken.None);

        // Check level fixed
        for (ulong i = 0; i < numberOfBlocks; ++i)
        {
            bool isBroken = brokenLevel < numberOfBlocks
                && i == brokenLevel
                && firstBlock <= brokenLevel
                && brokenLevel <= (lastBlock ?? numberOfBlocks);

            if (isBroken)
            {
                Assert.That(persistedLevels[i].BlockInfos[0].TotalDifficulty, Is.EqualTo((UInt256)expectedTd));
            }
            else
            {
                Assert.That(persistedLevels[i], Is.EqualTo(null));
            }
        }
    }

    [Test]
    public async Task should_fix_non_canonical()
    {
        Dictionary<Hash256, BlockHeader> hashesToHeaders = [];
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
        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(info => hashesToHeaders[(Hash256)info[0]]);
        blockTree.BestKnownNumber.Returns(5UL);

        ChainLevelInfo[] persistedLevels = new ChainLevelInfo[5];
        IChainLevelInfoRepository chainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();
        chainLevelInfoRepository.LoadLevel(Arg.Any<ulong>()).Returns(info => levels[(ulong)info[0]]);
        chainLevelInfoRepository
            .When(x => x.PersistLevel(Arg.Any<ulong>(), Arg.Any<ChainLevelInfo>()))
            .Do(x => persistedLevels[(ulong)x[0]] = (ChainLevelInfo)x[1]);

        SyncConfig syncConfig = new()
        {
            FixTotalDifficulty = true,
            FixTotalDifficultyStartingBlock = 1,
            FixTotalDifficultyLastBlock = 4
        };
        TotalDifficultyFixMigration migration = new(chainLevelInfoRepository, blockTree, syncConfig, new TestLogManager());

        // Run
        await migration.Run(CancellationToken.None);

        Assert.That(persistedLevels[0], Is.Null);
        Assert.That(persistedLevels[1], Is.Null);
        Assert.That(persistedLevels[2], Is.Null);

        Assert.That(persistedLevels[3].BlockInfos.Length, Is.EqualTo(2));
        Assert.That(persistedLevels[3].BlockInfos[0].TotalDifficulty, Is.EqualTo((UInt256)10));
        Assert.That(persistedLevels[3].BlockInfos[1].TotalDifficulty, Is.EqualTo((UInt256)303));

        Assert.That(persistedLevels[4].BlockInfos.Length, Is.EqualTo(1));
        Assert.That(persistedLevels[4].BlockInfos[0].TotalDifficulty, Is.EqualTo((UInt256)15));
    }
}
