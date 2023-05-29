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
            Assert.Throws<IndexOutOfRangeException>(() => { list[-1] = FastBlockStatus.Pending; });
            Assert.Throws<IndexOutOfRangeException>(() => { list[1] = FastBlockStatus.Pending; });
        }

        [Test]
        public void Can_read_back_all_set_values()
        {
            const long length = 4096;

            FastBlockStatusList list = new(length);
            for (int i = 0; i < length; i++)
            {
                list[i] = (FastBlockStatus)(i % 3);
            }
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
                FastBlockStatusList list = new(len);
                Parallel.For(0, len, (i) =>
                {
                    list[i] = (FastBlockStatus)(i % 3);
                });
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
                FastBlockStatusList list = new(len);
                for (int i = 0; i < len; i++)
                {
                    list[i] = (FastBlockStatus)(i % 3);
                }

                for (int i = 0; i < len; i++)
                {
                    switch (list[i])
                    {
                        case FastBlockStatus.Pending:
                            Assert.IsFalse(list.TryMarkPending(i));
                            Assert.IsFalse(list.TryMarkInserted(i));
                            Assert.IsTrue(list.TryMarkSent(i));
                            goto case FastBlockStatus.Sent;

                        case FastBlockStatus.Sent:
                            Assert.IsFalse(list.TryMarkSent(i));
                            Assert.IsTrue(list.TryMarkPending(i));
                            Assert.IsTrue(list.TryMarkSent(i));
                            Assert.IsTrue(list.TryMarkInserted(i));
                            goto case FastBlockStatus.Inserted;

                        case FastBlockStatus.Inserted:
                            Assert.IsFalse(list.TryMarkPending(i));
                            Assert.IsFalse(list.TryMarkSent(i));
                            Assert.IsFalse(list.TryMarkInserted(i));
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
                FastBlockStatusList list = new(len);
                Parallel.For(0, len, (i) =>
                {
                    list[i] = (FastBlockStatus)(i % 3);
                });
                Parallel.For(0, len, (i) =>
                {
                    switch (list[i])
                    {
                        case FastBlockStatus.Pending:
                            Assert.IsFalse(list.TryMarkPending(i));
                            Assert.IsFalse(list.TryMarkInserted(i));
                            Assert.IsTrue(list.TryMarkSent(i));
                            goto case FastBlockStatus.Sent;

                        case FastBlockStatus.Sent:
                            Assert.IsFalse(list.TryMarkSent(i));
                            Assert.IsTrue(list.TryMarkPending(i));
                            Assert.IsTrue(list.TryMarkSent(i));
                            Assert.IsTrue(list.TryMarkInserted(i));
                            goto case FastBlockStatus.Inserted;

                        case FastBlockStatus.Inserted:
                            Assert.IsFalse(list.TryMarkPending(i));
                            Assert.IsFalse(list.TryMarkSent(i));
                            Assert.IsFalse(list.TryMarkInserted(i));
                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                });
            }
        }
    }
}
