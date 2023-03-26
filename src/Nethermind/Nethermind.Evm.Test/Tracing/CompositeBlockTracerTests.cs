// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    public class CompositeBlockTracerTests
    {
        [Test]
        public void Should_create_tracer_correctly()
        {
            Keccak txHash = TestItem.KeccakA;
            GethLikeBlockMemoryTracer gethLikeBlockTracer = new(GethTraceOptions.Default with { TxHash = txHash });
            ParityLikeBlockTracer parityLikeBlockTracer = new(txHash, ParityTraceTypes.All);

            CompositeBlockTracer compositeBlockTracer = new CompositeBlockTracer();
            compositeBlockTracer.AddRange(gethLikeBlockTracer, parityLikeBlockTracer);

            compositeBlockTracer.IsTracingRewards.Should().Be(true);
        }

        [Test]
        public void Should_trace_properly()
        {
            Block block = Build.A.Block.TestObject;
            Transaction tx1 = Build.A.Transaction.TestObject;
            Transaction tx2 = Build.A.Transaction.TestObject;
            Transaction tx3 = Build.A.Transaction.TestObject;

            block = block.WithReplacedBody(new BlockBody(new[] { tx1, tx2, tx3 }, new BlockHeader[0]));

            GethLikeBlockMemoryTracer gethLikeBlockTracer = new(GethTraceOptions.Default);
            ParityLikeBlockTracer parityLikeBlockTracer = new(ParityTraceTypes.All);
            NullBlockTracer nullBlockTracer = NullBlockTracer.Instance;
            AlwaysCancelBlockTracer alwaysCancelBlockTracer = AlwaysCancelBlockTracer.Instance;

            CompositeBlockTracer blockTracer = new CompositeBlockTracer();
            blockTracer.AddRange(gethLikeBlockTracer, parityLikeBlockTracer, nullBlockTracer, alwaysCancelBlockTracer);

            blockTracer.StartNewBlockTrace(block);

            blockTracer.StartNewTxTrace(tx1);
            blockTracer.EndTxTrace();

            blockTracer.StartNewTxTrace(tx2);
            blockTracer.EndTxTrace();

            blockTracer.StartNewTxTrace(tx3);
            blockTracer.EndTxTrace();

            blockTracer.EndBlockTrace();

            IReadOnlyCollection<GethLikeTxTrace> gethResult = gethLikeBlockTracer.BuildResult();
            gethResult.Count.Should().Be(3);

            IReadOnlyCollection<ParityLikeTxTrace> parityResult = parityLikeBlockTracer.BuildResult();
            parityResult.Count.Should().Be(3);
        }
    }
}
