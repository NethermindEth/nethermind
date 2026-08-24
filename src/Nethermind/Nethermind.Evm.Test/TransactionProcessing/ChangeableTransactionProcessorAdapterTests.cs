// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.TransactionProcessing;

public class ChangeableTransactionProcessorAdapterTests
{
    /// <summary>
    /// #12723: a worker built by <see cref="ChangeableTransactionProcessorAdapter.ForProcessor"/> must re-read the
    /// shared adapter's mode on every call, not snapshot it at build time — the EIP-7928 BAL pool builds its worker
    /// once, before <see cref="Nethermind.Consensus.Tracing.GethStyleTracer"/> swaps the mode, and reuses it across
    /// later (non-swapping) traces that must fall back to Execute.
    /// </summary>
    [Test]
    public void ForProcessor_worker_reflects_the_current_mode_on_every_call()
    {
        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        ChangeableTransactionProcessorAdapter changeable = new(processor);
        ITransactionProcessorAdapter worker = changeable.ForProcessor(processor);

        Transaction tx = Build.A.Transaction.TestObject;
        ITxTracer tracer = NullTxTracer.Instance;

        // Default mode validates (Execute → Commit).
        worker.Execute(tx, tracer);
        processor.Received(1).Process(tx, tracer, ExecutionOptions.Commit);

        // The tracer swaps the shared adapter to Trace; the same worker must skip validation from now on.
        // A worker that snapshotted the mode at ForProcessor time would still run Commit here.
        changeable.CurrentAdapter = new TraceTransactionProcessorAdapter(processor);
        worker.Execute(tx, tracer);
        processor.Received(1).Process(tx, tracer, ExecutionOptions.SkipValidationAndCommit);
    }
}
