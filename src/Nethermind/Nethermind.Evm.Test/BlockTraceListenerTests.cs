/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class BlockTraceListenerTests
    {
        [Test]
        public void Number_of_tx_traces_equals_number_of_txs_in_a_block()
        {
            Block block = Build.A.Block.TestObject;
            block.Transactions = new Transaction[3];

            BlockBlockTracer blockTracer = new BlockBlockTracer(block);
            Assert.AreEqual(3, blockTracer.GethLikeBlockTrace.TxTraces.Length);
        }

        [Test]
        public void Records_trace_properly()
        {
            Block block = Build.A.Block.TestObject;
            block.Transactions = new Transaction[3];

            BlockBlockTracer blockTracer = new BlockBlockTracer(block);
            GethLikeTxTrace a = new GethLikeTxTrace();
            GethLikeTxTrace b = new GethLikeTxTrace();
            GethLikeTxTrace c = new GethLikeTxTrace();

            blockTracer.RecordTrace(TestObject.KeccakA, a);
            blockTracer.RecordTrace(TestObject.KeccakB, b);
            blockTracer.RecordTrace(TestObject.KeccakC, c);

            Assert.AreSame(a, blockTracer.GethLikeBlockTrace.TxTraces[0], "0");
            Assert.AreSame(b, blockTracer.GethLikeBlockTrace.TxTraces[1], "1");
            Assert.AreSame(c, blockTracer.GethLikeBlockTrace.TxTraces[2], "2");
        }

        [Test]
        public void Should_trace_responds_properly()
        {
            Block block = Build.A.Block.TestObject;
            block.Transactions = new Transaction[3];

            BlockBlockTracer blockTracer = new BlockBlockTracer(block);
            Assert.True(blockTracer.ShouldTrace(TestObject.KeccakH));
        }

        [Test]
        public void Throws_when_record_called_too_many_times()
        {
            Block block = Build.A.Block.TestObject;
            block.Transactions = new Transaction[3];

            BlockBlockTracer blockTracer = new BlockBlockTracer(block);
            GethLikeTxTrace a = new GethLikeTxTrace();

            blockTracer.RecordTrace(TestObject.KeccakA, a);
            blockTracer.RecordTrace(TestObject.KeccakA, a);
            blockTracer.RecordTrace(TestObject.KeccakA, a);

            Assert.Throws<InvalidOperationException>(() => blockTracer.RecordTrace(TestObject.KeccakA, a));
        }
    }
}