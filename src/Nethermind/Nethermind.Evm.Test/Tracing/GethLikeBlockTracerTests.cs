//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    public class GethLikeBlockTracerTests
    {
        [Test]
        public void Starts_with_trace_set_to_null()
        {
            Keccak txHash = TestItem.KeccakA;
            GethLikeBlockTracer blockTracer = new(txHash, GethTraceOptions.Default);
            Assert.IsNull(blockTracer.BuildResult().SingleOrDefault(), $"starts with trace set to null");
        }

        [Test]
        public void Number_of_tx_traces_equals_number_of_txs_in_a_block()
        {
            Block block = Build.A.Block.TestObject;
            block = block.WithReplacedBody(new BlockBody(new Transaction[3], new BlockHeader[0]));

            GethLikeBlockTracer blockTracer = new(GethTraceOptions.Default);

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                ((IBlockTracer) blockTracer).StartNewTxTrace(Build.A.Transaction.TestObject);
                ((IBlockTracer) blockTracer).EndTxTrace();    
            }
            
            Assert.AreEqual(3, blockTracer.BuildResult().Count);
        }

        [Test]
        public void Records_trace_properly()
        {
            Block block = Build.A.Block.TestObject;
            block = block.WithReplacedBody(new BlockBody(new Transaction[3], new BlockHeader[0]));

            GethLikeBlockTracer blockTracer = new(GethTraceOptions.Default);
            ((IBlockTracer) blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject);
            ((IBlockTracer) blockTracer).EndTxTrace();

            ((IBlockTracer) blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).TestObject);
            ((IBlockTracer) blockTracer).EndTxTrace();

            ((IBlockTracer) blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).TestObject);
            ((IBlockTracer) blockTracer).EndTxTrace();

            Assert.NotNull(blockTracer.BuildResult().First(), "0");
            Assert.NotNull(blockTracer.BuildResult().Skip(1).First(), "1");
            Assert.NotNull(blockTracer.BuildResult().Last(), "2");
        }
        
        [Test]
        [Ignore("It is not the actual behaviour at the moment")]
        public void Throws_when_ending_without_starting()
        {
            Block block = Build.A.Block.TestObject;
            block = block.WithReplacedBody(new BlockBody(new Transaction[3], new BlockHeader[0]));
            block.Transactions[0] = Build.A.Transaction.TestObject;
            block.Transactions[1] = Build.A.Transaction.TestObject;
            block.Transactions[2] = Build.A.Transaction.TestObject;

            GethLikeBlockTracer blockTracer1 = new(GethTraceOptions.Default);
            Assert.Throws<InvalidOperationException>(() => ((IBlockTracer)blockTracer1).EndTxTrace());
            
            GethLikeBlockTracer blockTracer2 = new(GethTraceOptions.Default);
            ((IBlockTracer)blockTracer2).StartNewTxTrace(block.Transactions[0]);
            Assert.DoesNotThrow(() => ((IBlockTracer)blockTracer2).EndTxTrace());
            Assert.Throws<InvalidOperationException>(() => ((IBlockTracer)blockTracer2).EndTxTrace());
        }
    }
}
