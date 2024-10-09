// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            Assert.That((FastBlockStatus)(i % 3) == list[i], Is.True);
        }
    }

    [Test]
    public void Will_not_go_below_ancient_barrier()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindCanonicalBlockInfo(Arg.Any<long>()).Returns(new BlockInfo(TestItem.KeccakA, 0));
        SyncStatusList syncStatusList = new SyncStatusList(blockTree, 1000, null, 900);

        BlockInfo?[] infos = new BlockInfo?[500];
        syncStatusList.GetInfosForBatch(infos);

        infos.Count((it) => it is not null).Should().Be(101);
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
                Assert.That((FastBlockStatus)(i % 3) == list[i], Is.True);
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
        new(Enumerable.Range(0, length).Select(i => (FastBlockStatus)(i % 3)).ToList(), parallel);
}
