// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Test.Helpers;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Crypto;
using Nethermind.Int256;
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

    [Test]
    public void Executing_with_no_recovered_senders_reaches_the_same_state_root() =>
        Assert.That(ExecuteTransfers(withSenders: false), Is.EqualTo(ExecuteTransfers(withSenders: true)));

    /// <summary>
    /// Runs the same transfers twice: once with the senders a completed recovery would have supplied, and once
    /// with none of them, which is the worst case of processing a block while recovery is still running.
    /// </summary>
    private static Hash256 ExecuteTransfers(bool withSenders)
    {
        PrivateKey[] signers = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC, TestItem.PrivateKeyD];
        using EvmTestHarness harness = new(Prague.Instance);
        foreach (PrivateKey signer in signers)
        {
            harness.WorldState.CreateAccount(signer.Address, 1_000_000);
        }

        Transaction[] txs = new Transaction[signers.Length];
        for (int i = 0; i < signers.Length; i++)
        {
            txs[i] = Build.A.Transaction
                .WithTo(TestItem.AddressF)
                .WithValue((UInt256)(i + 1))
                .WithGasLimit(GasCostOf.Transaction)
                .Signed(harness.Ecdsa, signers[i])
                .WithSenderAddress(withSenders ? signers[i].Address : null)
                .TestObject;
        }

        Block block = harness.CreateBlock(txs);
        foreach (Transaction tx in txs)
        {
            harness.ExecuteTx(tx, block);
        }

        IReleaseSpec spec = harness.SpecProvider.GetSpec(block.Header);
        harness.WorldState.Commit(spec, NullStateTracer.Instance);
        harness.WorldState.CommitTree(block.Number);
        return harness.WorldState.StateRoot;
    }
}
