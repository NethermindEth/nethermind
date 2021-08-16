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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using NSubstitute;
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
            GethLikeBlockTracer gethLikeBlockTracer = new(txHash, GethTraceOptions.Default);
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

            block = block.WithReplacedBody(new BlockBody(new []{tx1, tx2, tx3}, new BlockHeader[0]));

            GethLikeBlockTracer gethLikeBlockTracer = new(GethTraceOptions.Default);
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

            var gethResult = gethLikeBlockTracer.BuildResult();
            gethResult.Count.Should().Be(3);
            
            var parityResult = parityLikeBlockTracer.BuildResult();
            parityResult.Count.Should().Be(3);
        }
    }
}
