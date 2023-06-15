// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
                Assert.IsTrue((FastBlockStatus)(i % 3) == list[i]);
            }
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
                    Assert.IsTrue((FastBlockStatus)(i % 3) == list[i]);
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
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Pending));
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Inserted));
                            Assert.IsTrue(list.TrySet(i, FastBlockStatus.Sent));
                            goto case FastBlockStatus.Sent;

                        case FastBlockStatus.Sent:
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Sent));
                            Assert.IsTrue(list.TrySet(i, FastBlockStatus.Pending));
                            Assert.IsTrue(list.TrySet(i, FastBlockStatus.Sent));
                            Assert.IsTrue(list.TrySet(i, FastBlockStatus.Inserted));
                            goto case FastBlockStatus.Inserted;

                        case FastBlockStatus.Inserted:
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Pending));
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Sent));
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Inserted));
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
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Pending));
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Inserted));
                            Assert.IsTrue(list.TrySet(i, FastBlockStatus.Sent));
                            goto case FastBlockStatus.Sent;

                        case FastBlockStatus.Sent:
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Sent));
                            Assert.IsTrue(list.TrySet(i, FastBlockStatus.Pending));
                            Assert.IsTrue(list.TrySet(i, FastBlockStatus.Sent));
                            Assert.IsTrue(list.TrySet(i, FastBlockStatus.Inserted));
                            goto case FastBlockStatus.Inserted;

                        case FastBlockStatus.Inserted:
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Pending));
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Sent));
                            Assert.IsFalse(list.TrySet(i, FastBlockStatus.Inserted));
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
}
