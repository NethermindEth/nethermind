// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Synchronization.FastBlocks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastBlocks;

[Parallelizable(ParallelScope.Self)]
public class SyncStatusListTests
{
    [Test]
    public void Out_of_range_access_throws()
    {
        FastBlockStatusList list = new(1);

        FastBlockStatus a = list[0];
        list.TrySet(0, a);

        Assert.Throws<IndexOutOfRangeException>(() => { FastBlockStatus a = list[-1]; });
        Assert.Throws<IndexOutOfRangeException>(() => { FastBlockStatus a = list[1]; });
        Assert.Throws<IndexOutOfRangeException>(() => { list.TrySet(-1, FastBlockStatus.Pending); });
        Assert.Throws<IndexOutOfRangeException>(() => { list.TrySet(1, FastBlockStatus.Pending); });
    }

    [Test]
    public void Can_read_back_all_set_values()
    {
        const int length = 4096;

        FastBlockStatusList list = CreateFastBlockStatusList(length, false);
        for (int i = 0; i < length; i++)
        {
            Assert.That((FastBlockStatus)(i % 3), Is.EqualTo(list[i]));
        }
    }

    [Test]
    public void Will_not_go_below_ancient_barrier()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindCanonicalBlockInfo(Arg.Any<long>()).Returns(new BlockInfo(TestItem.KeccakA, 0));
        SyncStatusList syncStatusList = new(blockTree, 1000, null, 900);

        syncStatusList.TryGetInfosForBatch(500, new NoDownloadStrategy(), out BlockInfo?[] infos);

        infos.Count(static (it) => it is not null).Should().Be(101);
    }

    [Test]
    public void Will_skip_existing_keys()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindCanonicalBlockInfo(Arg.Any<long>())
            .Returns((ci) =>
            {
                long blockNumber = (long)ci[0];
                return new BlockInfo(TestItem.KeccakA, 0)
                {
                    BlockNumber = blockNumber
                };
            });

        SyncStatusList syncStatusList = new(blockTree, 100000, null, 1000);

        ConstantDownloadStrategy downloadStrategy = new([99999, 99995, 99950, 99000, 99001, 99003, 85000]);

        List<long> TryGetInfos()
        {
            syncStatusList.TryGetInfosForBatch(50, downloadStrategy, out BlockInfo?[] infos);
            return [.. infos.Where(bi => bi != null).Select((bi) => bi!.BlockNumber)];
        }

        TryGetInfos().Should().BeEquivalentTo([99999, 99995]); // first two as it will try the first 50 only
        TryGetInfos().Should().BeEquivalentTo([99950]); // Then the next 50
        TryGetInfos().Should().BeEquivalentTo([99000, 99001, 99003]); // If the next 50 failed, it will try looking far back.
        TryGetInfos().Should().BeEmpty(); // If it look far back enough and still does not find anything it will just return so that progress can update.
        TryGetInfos().Should().BeEquivalentTo([85000]); // But as the existing blocks was already marked as inserted, it should be able to make progress on later call.
    }

    [Test]
    public void Can_read_back_all_parallel_set_values()
    {
        const long length = 4096;

        for (var len = 0; len < length; len++)
        {
            FastBlockStatusList list = CreateFastBlockStatusList(len);
            Parallel.For(0, len, (i) =>
            {
                Assert.That((FastBlockStatus)(i % 3), Is.EqualTo(list[i]));
            });
        }
    }

    [Test]
    public void State_transitions_are_enforced()
    {
        const long length = 4096;

        for (var len = 0; len < length; len++)
        {
            FastBlockStatusList list = CreateFastBlockStatusList(len, false);
            for (int i = 0; i < len; i++)
            {
                switch (list[i])
                {
                    case FastBlockStatus.Pending:
                        Assert.That(list.TrySet(i, FastBlockStatus.Pending), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Inserted), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Sent), Is.True);
                        goto case FastBlockStatus.Sent;

                    case FastBlockStatus.Sent:
                        Assert.That(list.TrySet(i, FastBlockStatus.Sent), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Pending), Is.True);
                        Assert.That(list.TrySet(i, FastBlockStatus.Sent), Is.True);
                        Assert.That(list.TrySet(i, FastBlockStatus.Inserted), Is.True);
                        goto case FastBlockStatus.Inserted;

                    case FastBlockStatus.Inserted:
                        Assert.That(list.TrySet(i, FastBlockStatus.Pending), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Sent), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Inserted), Is.False);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
    }

    [Test]
    public void State_transitions_are_enforced_in_parallel()
    {
        const long length = 4096;

        for (var len = 0; len < length; len++)
        {
            FastBlockStatusList list = CreateFastBlockStatusList(len);
            Parallel.For(0, len, (i) =>
            {
                switch (list[i])
                {
                    case FastBlockStatus.Pending:
                        Assert.That(list.TrySet(i, FastBlockStatus.Pending), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Inserted), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Sent), Is.True);
                        goto case FastBlockStatus.Sent;

                    case FastBlockStatus.Sent:
                        Assert.That(list.TrySet(i, FastBlockStatus.Sent), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Pending), Is.True);
                        Assert.That(list.TrySet(i, FastBlockStatus.Sent), Is.True);
                        Assert.That(list.TrySet(i, FastBlockStatus.Inserted), Is.True);
                        goto case FastBlockStatus.Inserted;

                    case FastBlockStatus.Inserted:
                        Assert.That(list.TrySet(i, FastBlockStatus.Pending), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Sent), Is.False);
                        Assert.That(list.TrySet(i, FastBlockStatus.Inserted), Is.False);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            });
        }
    }

    private static FastBlockStatusList CreateFastBlockStatusList(int length, bool parallel = true) =>
        new(Enumerable.Range(0, length).Select(static i => (FastBlockStatus)(i % 3)).ToList(), parallel);

    private class NoDownloadStrategy : IBlockDownloadStrategy
    {
        public bool ShouldDownloadBlock(BlockInfo info) => true;
    }

    private class ConstantDownloadStrategy(HashSet<long> needToFetchBlocks) : IBlockDownloadStrategy
    {
        public bool ShouldDownloadBlock(BlockInfo info)
            => needToFetchBlocks.Contains(info.BlockNumber);
    }
}
