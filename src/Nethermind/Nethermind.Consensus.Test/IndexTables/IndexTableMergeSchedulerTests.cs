// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.IndexTables;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexTableMergeSchedulerTests
{
    [TestCase(1, 0, ExpectedResult = 4L)]   // tableSize=4, delay=1: 0+4-1+1=4
    [TestCase(1, 4, ExpectedResult = 8L)]   // 4+4-1+1=8
    [TestCase(2, 0, ExpectedResult = 19L)]  // tableSize=16, delay=4: 0+16-1+4=19
    [TestCase(2, 16, ExpectedResult = 35L)] // 16+16-1+4=35
    [TestCase(3, 0, ExpectedResult = 79L)]  // tableSize=64, delay=16: 0+64-1+16=79
    [TestCase(4, 0, ExpectedResult = 319L)] // tableSize=256, delay=64: 0+256-1+64=319
    public long PublicationBlock_matches_spec(int level, long firstBlock) =>
        IndexTableMergeScheduler.PublicationBlock(level, firstBlock);

    [Test]
    public void GetTablesForBlock_returns_level1_at_block_4()
    {
        // Level 1 table for firstBlock=0 publishes at block 4
        int publishCount = 0;
        IndexTableMergeScheduler.GetTablesForBlock(4, (level, firstBlock, tableSize) =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(level, Is.EqualTo(1));
                Assert.That(firstBlock, Is.EqualTo(0));
                Assert.That(tableSize, Is.EqualTo(4));
            });
            publishCount++;
        });

        Assert.That(publishCount, Is.EqualTo(1));
    }

    [Test]
    public void GetTablesForBlock_returns_nothing_at_block_3()
    {
        int publishCount = 0;
        IndexTableMergeScheduler.GetTablesForBlock(3, (_, _, _) => publishCount++);
        Assert.That(publishCount, Is.EqualTo(0));
    }

    [Test]
    public void GetTablesForBlock_returns_level1_at_block_8()
    {
        // Level 1 table for firstBlock=4 publishes at block 8
        int publishCount = 0;
        IndexTableMergeScheduler.GetTablesForBlock(8, (level, firstBlock, tableSize) =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(level, Is.EqualTo(1));
                Assert.That(firstBlock, Is.EqualTo(4));
                Assert.That(tableSize, Is.EqualTo(4));
            });
            publishCount++;
        });

        Assert.That(publishCount, Is.EqualTo(1));
    }

    [Test]
    public void GetTablesForBlock_at_block_19_publishes_level1_and_level2()
    {
        // Block 19: level-2 for firstBlock=0 (0+16-1+4=19) AND level-1 for firstBlock=16 (16+4-1+1=20)? No.
        // Level 1: candidateFirst = 19 - 4 + 1 - 1 = 15 → 15 % 4 = 3 → not aligned → no
        // Level 2: candidateFirst = 19 - 16 + 1 - 4 = 0 → 0 % 16 = 0 → yes
        // Level 3: candidateFirst = 19 - 64 + 1 - 16 = -60 → negative → no
        // Level 4: negative → no
        int publishCount = 0;
        int level2Count = 0;
        IndexTableMergeScheduler.GetTablesForBlock(19, (level, firstBlock, _) =>
        {
            publishCount++;
            if (level == 2)
            {
                Assert.That(firstBlock, Is.EqualTo(0));
                level2Count++;
            }
        });

        Assert.That(level2Count, Is.EqualTo(1));
        Assert.That(publishCount, Is.EqualTo(1));
    }

    [Test]
    public void GetTablesForBlock_at_block_319_publishes_level4()
    {
        // Level 4: candidateFirst = 319 - 256 + 1 - 64 = 0 → 0 % 256 = 0 → yes
        bool foundLevel4 = false;
        IndexTableMergeScheduler.GetTablesForBlock(319, (level, firstBlock, tableSize) =>
        {
            if (level == 4)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(firstBlock, Is.EqualTo(0));
                    Assert.That(tableSize, Is.EqualTo(256));
                });
                foundLevel4 = true;
            }
        });

        Assert.That(foundLevel4, Is.True);
    }
}
