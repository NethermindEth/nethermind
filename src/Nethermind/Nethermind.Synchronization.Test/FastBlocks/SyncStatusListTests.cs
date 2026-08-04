// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        FastBlockStatusList list = new(1UL);

        FastBlockStatus a = list[0UL];
        list.TrySet(0UL, a);

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<IndexOutOfRangeException>(() => { FastBlockStatus a = list[ulong.MaxValue]; });
            Assert.Throws<IndexOutOfRangeException>(() => { FastBlockStatus a = list[1UL]; });
            Assert.Throws<IndexOutOfRangeException>(() => { list.TrySet(ulong.MaxValue, FastBlockStatus.Pending); });
            Assert.Throws<IndexOutOfRangeException>(() => { list.TrySet(1UL, FastBlockStatus.Pending); });
        }
    }

    [Test]
    public void Can_read_back_all_set_values()
    {
        const int length = 4096;

        FastBlockStatusList list = CreateFastBlockStatusList(length, false);
        for (ulong i = 0; i < length; i++)
        {
            Assert.That((FastBlockStatus)(i % 3), Is.EqualTo(list[i]));
        }
    }

    [Test]
    public void Will_not_go_below_ancient_barrier()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindCanonicalBlockInfo(Arg.Any<ulong>()).Returns(new BlockInfo(TestItem.KeccakA, 0));
        SyncStatusList syncStatusList = new(blockTree, 1000, null, 900);

        syncStatusList.TryGetInfosForBatch(500, new AlwaysDownloadStrategy(), out BlockInfo?[] infos);

        Assert.That(infos.Count(static (it) => it is not null), Is.EqualTo(101));
    }

    [Test]
    public void Will_skip_existing_keys()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindCanonicalBlockInfo(Arg.Any<ulong>())
            .Returns(ci =>
            {
                ulong blockNumber = ci.ArgAt<ulong>(0);
                return new BlockInfo(TestItem.KeccakA, 0)
                {
                    BlockNumber = blockNumber
                };
            });

        SyncStatusList syncStatusList = new(blockTree, 100000, null, 1000);

        ConstantDownloadStrategy downloadStrategy = new([99999UL, 99995UL, 99950UL, 99000UL, 99001UL, 99003UL, 85000UL]);

        List<ulong> TryGetInfos()
        {
            syncStatusList.TryGetInfosForBatch(50, downloadStrategy, out BlockInfo?[] infos);
            return [.. infos.Where(bi => bi != null).Select((bi) => bi!.BlockNumber)];
        }

        Assert.That(TryGetInfos(), Is.EquivalentTo([99999UL, 99995UL])); // first two as it will try the first 50 only
        Assert.That(TryGetInfos(), Is.EquivalentTo([99950UL])); // Then the next 50
        Assert.That(TryGetInfos(), Is.EquivalentTo([99000UL, 99001UL, 99003UL])); // If the next 50 failed, it will try looking far back.
        Assert.That(TryGetInfos(), Is.Empty); // If it look far back enough and still does not find anything it will just return so that progress can update.
        Assert.That(TryGetInfos(), Is.EquivalentTo([85000UL])); // But as the existing blocks was already marked as inserted, it should be able to make progress on later call.
    }

    [Test]
    public void Transiently_unresolvable_block_is_retried_instead_of_orphaned_in_sent()
    {
        const ulong unsettled = 95;
        bool settled = false;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindCanonicalBlockInfo(Arg.Any<ulong>())
            .Returns(ci =>
            {
                ulong blockNumber = ci.ArgAt<ulong>(0);
                return blockNumber == unsettled && !settled
                    ? null
                    : new BlockInfo(TestItem.KeccakA, 0) { BlockNumber = blockNumber };
            });

        SyncStatusList syncStatusList = new(blockTree, 100, null, 90);

        List<ulong> GetAndInsert()
        {
            syncStatusList.TryGetInfosForBatch(50, new AlwaysDownloadStrategy(), out BlockInfo?[] infos);
            List<ulong> numbers = [.. infos.Where(static bi => bi is not null).Select(static bi => bi!.BlockNumber)];
            foreach (ulong number in numbers)
            {
                syncStatusList.MarkInserted(number);
            }
            return numbers;
        }

        Assert.That(GetAndInsert(), Does.Not.Contain(unsettled));
        settled = true;
        Assert.That(GetAndInsert(), Does.Contain(unsettled));

        syncStatusList.TryGetInfosForBatch(50, new AlwaysDownloadStrategy(), out _);
        Assert.That(syncStatusList.LowestInsertWithoutGaps, Is.LessThan(unsettled));
    }

    [Test]
    public void Can_read_back_all_parallel_set_values()
    {
        const long length = 4096;

        for (int len = 0; len < length; len++)
        {
            FastBlockStatusList list = CreateFastBlockStatusList(len);
            Parallel.For(0, len, (i) =>
            {
                ulong idx = (ulong)i;
                Assert.That((FastBlockStatus)(idx % 3), Is.EqualTo(list[idx]));
            });
        }
    }

    [Test]
    public void State_transitions_are_enforced()
    {
        const long length = 4096;

        for (int len = 0; len < length; len++)
        {
            FastBlockStatusList list = CreateFastBlockStatusList(len, false);
            ulong ulen = (ulong)len;
            for (ulong i = 0; i < ulen; i++)
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

        for (int len = 0; len < length; len++)
        {
            FastBlockStatusList list = CreateFastBlockStatusList(len);
            Parallel.For(0, len, (rawI) =>
            {
                ulong i = (ulong)rawI;
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
        new([.. Enumerable.Range(0, length).Select(static i => (FastBlockStatus)(i % 3))], parallel);

    private class AlwaysDownloadStrategy : IBlockDownloadStrategy
    {
        public bool ShouldDownloadBlock(BlockInfo info) => true;
    }

    private class ConstantDownloadStrategy(HashSet<ulong> needToFetchBlocks) : IBlockDownloadStrategy
    {
        public bool ShouldDownloadBlock(BlockInfo info)
            => needToFetchBlocks.Contains(info.BlockNumber);
    }
}
