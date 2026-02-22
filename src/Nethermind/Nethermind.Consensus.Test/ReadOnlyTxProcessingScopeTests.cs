// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class ReadOnlyTxProcessingScopeTests
{
    [Test]
    public void Test_WhenDispose_ThenStateRootWillReset()
    {
        bool closed = false;
        IDisposable closer = new Reactive.AnonymousDisposable(() => closed = true);
        ReadOnlyTxProcessingScope env = new ReadOnlyTxProcessingScope(
            Substitute.For<ITransactionProcessor>(),
            closer,
            Substitute.For<IWorldState>());

        env.Dispose();

        closed.Should().BeTrue();
    }

    [Test]
    public void ProcessTransaction_WhenDirectAccountDoesNotExist_DoesNotReadThroughNonceCache()
    {
        ITransactionProcessorAdapter transactionProcessor = Substitute.For<ITransactionProcessorAdapter>();
        TransactionResult executeResult = new();
        transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(executeResult);

        IWorldState stateProvider = Substitute.For<IWorldState>();
        Transaction currentTx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
        currentTx.Nonce = UInt256.One;
        stateProvider.GetAccountDirect(TestItem.AddressA).Returns((Account)null);

        BlockReceiptsTracer receiptsTracer = new();

        transactionProcessor.ProcessTransaction(
            currentTx,
            receiptsTracer,
            ProcessingOptions.LoadNonceFromState,
            stateProvider);

        currentTx.Nonce.Should().Be(UInt256.Zero);
        stateProvider.Received(1).GetAccountDirect(TestItem.AddressA);
        stateProvider.DidNotReceive().GetNonce(TestItem.AddressA);
    }
}
