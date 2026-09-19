// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Test.Helpers;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>Blocks are processed while sender recovery may still be running, so the processor must resolve a missing sender itself.</summary>
[TestFixture]
public class SenderRecoveryTests
{
    [Test]
    public void Signed_transaction_without_sender_is_recovered_before_execution()
    {
        using EvmTestHarness harness = new(Prague.Instance);
        harness.WorldState.CreateAccount(TestItem.AddressA, 1_000_000);
        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit(GasCostOf.Transaction)
            .Signed(harness.Ecdsa, TestItem.PrivateKeyA)
            .WithSenderAddress(null)
            .TestObject;
        Block block = harness.CreateBlock(tx);

        TransactionResult result = harness.TxProcessor.Execute(
            tx,
            new BlockExecutionContext(block.Header, harness.SpecProvider.GetSpec(block.Header)),
            NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.SenderAddress, Is.EqualTo(TestItem.AddressA));
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.None));
        }
    }

    [Test]
    public void Unsigned_transaction_without_sender_is_still_rejected()
    {
        using EvmTestHarness harness = new(Prague.Instance);
        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithGasLimit(GasCostOf.Transaction)
            .WithSenderAddress(null)
            .TestObject;
        Block block = harness.CreateBlock(tx);

        TransactionResult result = harness.TxProcessor.Execute(
            tx,
            new BlockExecutionContext(block.Header, harness.SpecProvider.GetSpec(block.Header)),
            NullTxTracer.Instance);

        Assert.That(result, Is.EqualTo(TransactionResult.SenderNotSpecified));
    }
}
