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
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Tracing;

public class GethStyleTracerTests
{
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
            Substitute.For<IOverridableEnv<GethStyleTracer.BlockProcessingComponents>>());

#pragma warning disable CS0618
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            tracer.Trace(block.Number, -1, GethTraceOptions.Default, CancellationToken.None));
#pragma warning restore CS0618

        Assert.That(exception.Message, Does.Contain("the requested tx index was -1"));
    }
}
