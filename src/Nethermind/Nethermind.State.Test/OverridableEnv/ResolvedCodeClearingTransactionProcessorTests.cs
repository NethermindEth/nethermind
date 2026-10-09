// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Store.Test.OverridableEnv;

public class ResolvedCodeClearingTransactionProcessorTests
{
    [TestCase(ExecutionOptions.CommitAndRestore, true)]
    [TestCase(ExecutionOptions.Commit, false)]
    [TestCase(ExecutionOptions.SkipValidationAndCommit, false)]
    [TestCase(ExecutionOptions.BuildUp, false)]
    [TestCase(ExecutionOptions.Warmup | ExecutionOptions.SkipValidation, false)]
    public void Keeps_resolved_code_only_after_a_restored_transaction_that_throws(ExecutionOptions options, bool kept)
    {
        // A transaction that keeps its changes may have removed code before it threw, so the next one starts empty.
        ITransactionProcessor inner = Substitute.For<ITransactionProcessor>();
        inner.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()).Throws(new OperationCanceledException());
        ResolvedCodeMemo memo = new();
        memo.Remember(TestItem.AddressA, new CodeInfo(new byte[] { 0x60, 0x00 }));
        ResolvedCodeClearingTransactionProcessor processor = new(inner, memo);

        Assert.Throws<OperationCanceledException>(() => processor.Process(new Transaction(), NullTxTracer.Instance, options));

        Assert.That(memo.TryGet(TestItem.AddressA, out _), Is.EqualTo(kept));
    }
}
