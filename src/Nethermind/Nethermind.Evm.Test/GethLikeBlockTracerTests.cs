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
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class GethLikeBlockTracerTests
    {
        [Test]
        public void Starts_with_trace_set_to_null()
        {
            Keccak txHash = TestObject.KeccakA;
            GethLikeBlockTracer blockTracer = new GethLikeBlockTracer(txHash);
            Assert.IsNull(blockTracer.BuildResult().TxTraces[0], $"starts with trace set to null");
        }

        [Test]
        public void Throws_when_recording_unexpected_trace()
        {
            Keccak txHash = TestObject.KeccakA;
            GethLikeBlockTracer blockTracer = new GethLikeBlockTracer(txHash);
            Assert.Throws<InvalidOperationException>(() => ((IBlockTracer) blockTracer).StartNewTxTrace(TestObject.KeccakB));
        }

        [Test]
        public void Number_of_tx_traces_equals_number_of_txs_in_a_block()
        {
            Block block = Build.A.Block.TestObject;
            block.Transactions = new Transaction[3];

            GethLikeBlockTracer blockTracer = new GethLikeBlockTracer(block);
            Assert.AreEqual(3, blockTracer.BuildResult().TxTraces.Length);
        }

        [Test]
        public void Records_trace_properly()
        {
            Block block = Build.A.Block.TestObject;
            block.Transactions = new Transaction[3];

            GethLikeBlockTracer blockTracer = new GethLikeBlockTracer(block);
            ((IBlockTracer) blockTracer).StartNewTxTrace(TestObject.KeccakA);
            ((IBlockTracer) blockTracer).EndTxTrace();

            ((IBlockTracer) blockTracer).StartNewTxTrace(TestObject.KeccakB);
            ((IBlockTracer) blockTracer).EndTxTrace();

            ((IBlockTracer) blockTracer).StartNewTxTrace(TestObject.KeccakC);
            ((IBlockTracer) blockTracer).EndTxTrace();

            Assert.NotNull(blockTracer.BuildResult().TxTraces[0], "0");
            Assert.NotNull(blockTracer.BuildResult().TxTraces[0], "1");
            Assert.NotNull(blockTracer.BuildResult().TxTraces[0], "2");
        }
        
        [Test]
        public void Throws_when_ending_without_starting()
        {
            Block block = Build.A.Block.TestObject;
            block.Transactions = new Transaction[3];
            block.Transactions[0] = Build.A.Transaction.TestObject;
            block.Transactions[1] = Build.A.Transaction.TestObject;
            block.Transactions[2] = Build.A.Transaction.TestObject;

            GethLikeBlockTracer blockTracer1 = new GethLikeBlockTracer(block);
            Assert.Throws<InvalidOperationException>(() => ((IBlockTracer)blockTracer1).EndTxTrace());
            
            GethLikeBlockTracer blockTracer2 = new GethLikeBlockTracer(block);
            ((IBlockTracer)blockTracer2).StartNewTxTrace(block.Transactions[0].Hash);
            Assert.DoesNotThrow(() => ((IBlockTracer)blockTracer2).EndTxTrace());
            Assert.Throws<InvalidOperationException>(() => ((IBlockTracer)blockTracer2).EndTxTrace());
        }
    }
}