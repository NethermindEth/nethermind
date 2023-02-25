// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

using DotNetty.Codecs;

using Nethermind.Synchronization.FastBlocks;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastBlocks
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SyncStatusListTests
    {
        [Test]
        public void Out_of_range_access_throws()
        {
            FastBlockStatusList list = new(1);

            FastBlockStatus a = list[0];
            list[0] = a;

            Assert.Throws<IndexOutOfRangeException>(() => { FastBlockStatus a = list[-1]; });
            Assert.Throws<IndexOutOfRangeException>(() => { FastBlockStatus a = list[1]; });
            Assert.Throws<IndexOutOfRangeException>(() => { list[-1] = FastBlockStatus.Unknown; });
            Assert.Throws<IndexOutOfRangeException>(() => { list[1] = FastBlockStatus.Unknown; });
        }

        [Test]
        public void Can_read_back_all_set_values()
        {
            for (var len = 0; len < 500; len++)
            {
                FastBlockStatusList list = new(len);
                for (int i = 0; i < len; i++)
                {
                    list[i] = (FastBlockStatus)(i % 3);
                }
                for (int i = 0; i < len; i++)
                {
                    Assert.IsTrue((FastBlockStatus)(i % 3) == list[i]);
                }
            }
        }

        [Test]
        public void Can_read_back_all_atomic_set_values()
        {
            for (var len = 0; len < 500; len++)
            {
                FastBlockStatusList list = new(len);
                for (int i = 0; i < len; i++)
                {
                    list.AtomicWrite(i, (FastBlockStatus)(i % 3));
                }
                for (int i = 0; i < len; i++)
                {
                    Assert.IsTrue((FastBlockStatus)(i % 3) == list[i]);
                }
            }
        }

        [Test]
        public void Can_read_back_all_parallel_set_values()
        {
            for (var len = 0; len < 500; len++)
            {
                FastBlockStatusList list = new(len);
                Parallel.For(0, len, (i) =>
                {
                    list.AtomicWrite(i, (FastBlockStatus)(i % 3));
                });
                Parallel.For(0, len, (i) =>
                {
                    Assert.IsTrue((FastBlockStatus)(i % 3) == list[i]);
                });
            }
        }
    }
}
