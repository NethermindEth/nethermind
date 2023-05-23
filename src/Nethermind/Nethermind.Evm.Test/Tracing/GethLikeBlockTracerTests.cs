// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.TestObject);
                ((IBlockTracer)blockTracer).EndTxTrace();
            }

            Assert.That(blockTracer.BuildResult().Count, Is.EqualTo(3));
        }

        [Test]
        public void Records_trace_properly()
        {
            Block block = Build.A.Block.TestObject;
            block = block.WithReplacedBody(new BlockBody(new Transaction[3], new BlockHeader[0]));

            GethLikeBlockTracer blockTracer = new(GethTraceOptions.Default);
            ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject);
            ((IBlockTracer)blockTracer).EndTxTrace();

            ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).TestObject);
            ((IBlockTracer)blockTracer).EndTxTrace();

            ((IBlockTracer)blockTracer).StartNewTxTrace(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).TestObject);
            ((IBlockTracer)blockTracer).EndTxTrace();

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
