// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class CompositeBlockTracerTests
{
    [Test]
    public void Should_create_tracer_correctly()
    {
        Hash256 txHash = TestItem.KeccakA;
        GethLikeBlockMemoryTracer gethLikeBlockTracer = new(GethTraceOptions.Default with { TxHash = txHash });
        ParityLikeBlockTracer parityLikeBlockTracer = new(txHash, ParityTraceTypes.All);

        CompositeBlockTracer compositeBlockTracer = new();
        compositeBlockTracer.AddRange(gethLikeBlockTracer, parityLikeBlockTracer);

        Assert.That(compositeBlockTracer.IsTracingRewards, Is.EqualTo(true));
    }

    [Test]
    public void Should_trace_properly()
    {
        Block block = Build.A.Block.TestObject;
        Transaction tx1 = Build.A.Transaction.TestObject;
        Transaction tx2 = Build.A.Transaction.TestObject;
        Transaction tx3 = Build.A.Transaction.TestObject;

        block = block.WithReplacedBody(new BlockBody(new[] { tx1, tx2, tx3 }, []));

        GethLikeBlockMemoryTracer gethLikeBlockTracer = new(GethTraceOptions.Default);
        ParityLikeBlockTracer parityLikeBlockTracer = new(ParityTraceTypes.All);
        NullBlockTracer nullBlockTracer = NullBlockTracer.Instance;
        AlwaysCancelBlockTracer alwaysCancelBlockTracer = AlwaysCancelBlockTracer.Instance;

        CompositeBlockTracer blockTracer = new();
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
        Assert.That(gethResult.Count, Is.EqualTo(3));

        IReadOnlyCollection<ParityLikeTxTrace> parityResult = parityLikeBlockTracer.BuildResult();
        Assert.That(parityResult.Count, Is.EqualTo(3));
    }

    [Test]
    public void StartNewTxTrace_returns_single_child_tracer_directly()
    {
        TestTxTracer txTracer = new();
        CompositeBlockTracer blockTracer = new();
        blockTracer.Add(new TestBlockTracer(txTracer));

        ITxTracer result = blockTracer.StartNewTxTrace(Build.A.Transaction.TestObject);

        Assert.That(result, Is.SameAs(txTracer));
    }

    [Test]
    public void StartNewTxTrace_returns_single_non_null_child_tracer_directly()
    {
        TestTxTracer txTracer = new();
        CompositeBlockTracer blockTracer = new();
        blockTracer.Add(NullBlockTracer.Instance);
        blockTracer.Add(new TestBlockTracer(txTracer));

        ITxTracer result = blockTracer.StartNewTxTrace(Build.A.Transaction.TestObject);

        Assert.That(result, Is.SameAs(txTracer));
    }

    [Test]
    public void GetParallelSafeTracer_cache_tracks_nested_composite_mutation()
    {
        CompositeBlockTracer parent = new();
        CompositeBlockTracer nested = new();
        parent.Add(nested);

        Assert.That(parent.GetParallelSafeTracer(), Is.SameAs(NullBlockTracer.Instance));

        ParallelSafeTestBlockTracer first = new();
        nested.Add(first);

        IBlockTracer firstResult = parent.GetParallelSafeTracer();
        Assert.That(firstResult, Is.SameAs(first));
        Assert.That(parent.GetParallelSafeTracer(), Is.SameAs(firstResult));

        nested.Add(new ParallelSafeTestBlockTracer());

        IBlockTracer secondResult = parent.GetParallelSafeTracer();
        Assert.That(secondResult, Is.TypeOf<CompositeBlockTracer>());
        Assert.That(secondResult, Is.Not.SameAs(firstResult));
    }

    private sealed class TestTxTracer : TxTracer;

    private class TestBlockTracer(ITxTracer? txTracer = null) : BlockTracer
    {
        private readonly ITxTracer _txTracer = txTracer ?? NullTxTracer.Instance;

        public override ITxTracer StartNewTxTrace(Transaction? tx) => _txTracer;
    }

    private sealed class ParallelSafeTestBlockTracer : TestBlockTracer, IParallelSafeBlockTracer;
}
