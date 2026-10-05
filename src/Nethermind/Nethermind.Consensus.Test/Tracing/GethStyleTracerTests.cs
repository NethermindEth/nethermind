// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Tracing;

public class GethStyleTracerTests
{
    [TestCase(null, 5)]
    [TestCase(2, 2)]
    public void Transaction_deadline_starts_with_target_and_defaults_to_five_seconds(int? timeoutSeconds, int expectedSeconds)
    {
        ManualTimeProvider clock = new();
        Transaction target = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;
        Transaction prefix = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(NullTxTracer.Instance);
        GethTraceOptions options = new()
        {
            TxHash = target.Hash,
            Timeout = timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null
        };
        using GethLikeBlockCallDeadlineTracer tracer = new(options, CancellationToken.None, _ =>
        {
            clock.AdvanceAndFireTimer(TimeSpan.FromSeconds(60));
            return inner;
        }, clock);
        tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(prefix, target).TestObject);
        using (tracer.StartNewTxTrace(prefix))
        {
            clock.AdvanceAndFireTimer(TimeSpan.FromSeconds(60));
            Assert.That(tracer.Expired, Is.False);
        }
        tracer.EndTxTrace();
        using (tracer.StartNewTxTrace(target))
        {
            clock.AdvanceAndFireTimer(TimeSpan.FromSeconds(expectedSeconds) - TimeSpan.FromTicks(1));
            Assert.That(tracer.Expired, Is.False);
            clock.AdvanceAndFireTimer(TimeSpan.FromTicks(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.Expired, Is.True);
                Assert.That(tracer.Token.IsCancellationRequested, Is.True);
                Assert.That(() => tracer.BuildResult(), Throws.TypeOf<TimeoutException>().With.Message.EqualTo("execution timeout"));
            }
        }
    }

    /// <remarks>
    /// The retained obsolete overload has no other in-tree caller, so its bounds check would regress
    /// unnoticed. The block has to carry a transaction: on an empty block every index is out of range,
    /// so the assertion would hold even if negative indices were not rejected.
    /// </remarks>
    [Test]
    public void Trace_by_block_number_rejects_a_negative_index()
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBlock(block.Number, BlockTreeLookupOptions.RequireCanonical).Returns(block);

        GethStyleTracer tracer = new(
            Substitute.For<IReceiptStorage>(),
            blockTree,
            Substitute.For<IBadBlockStore>(),
            Substitute.For<ISpecProvider>(),
            new ChangeableTransactionProcessorAdapter(Substitute.For<ITransactionProcessor>()),
            Substitute.For<IFileSystem>(),
            Substitute.For<IOverridableEnv<GethStyleTracer.BlockProcessingComponents>>(),
            NullPrefixStateSeedSource.Instance,
            Substitute.For<IOverridableCodeInfoRepository>());

#pragma warning disable CS0618
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            tracer.Trace(block.Number, -1, GethTraceOptions.Default, CancellationToken.None));
#pragma warning restore CS0618

        Assert.That(exception.Message, Does.Contain("the requested tx index was -1"));
    }

    /// <remarks>
    /// A tracer that is neither native nor loadable as JavaScript, an unknown name or inline code that does not
    /// compile, used to reach the JavaScript block tracer, which built a V8 engine per traced transaction only to
    /// fail loading the script.
    /// </remarks>
    [TestCase("flatCallTracer", false)]
    [TestCase("_bigInteger", true)]
    public void Create_options_tracer_defers_valid_javascript_expression_evaluation(string tracer, bool javascript)
    {
        using IDisposable result = (IDisposable)GethStyleTracer.CreateOptionsTracer(Build.A.BlockHeader.TestObject,
            GethTraceOptions.Default with { Tracer = tracer }, Substitute.For<IWorldState>(), Substitute.For<ISpecProvider>());
        Assert.That(result is Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript.GethLikeBlockJavaScriptTracer, Is.EqualTo(javascript));
    }

    [TestCase("{ ) }", "could not be compiled")]
    public void Create_options_tracer_rejects_an_unusable_tracer(string tracer, string expectedMessage)
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        GethTraceOptions options = GethTraceOptions.Default with { Tracer = tracer };

        Assert.That(
            () => GethStyleTracer.CreateOptionsTracer(header, options, Substitute.For<IWorldState>(), Substitute.For<ISpecProvider>()),
            Throws.ArgumentException.With.Message.Contains(expectedMessage));
    }
}
