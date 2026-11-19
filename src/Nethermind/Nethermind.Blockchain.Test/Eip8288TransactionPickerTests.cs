// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class Eip8288TransactionPickerTests
{
    /// <summary>The default picker with the EIP-8288 checks, as block production resolves it.</summary>
    internal static BlockProcessor.IBlockProductionTransactionPicker CreatePicker(IReleaseSpec spec, LeanProofStore? proofs = null)
    {
        TestSingleReleaseSpecProvider specProvider = new(spec);
        return new Eip8288TransactionPicker(new BlockProcessor.BlockProductionTransactionPicker(specProvider), specProvider,
            new BlocksConfig(), proofs ?? new LeanProofStore(FrameTxSigHash.ComputeValue));
    }

    // A plugin that replaces the picker, as Optimism does, must not drop the EIP-8288 checks.
    [Test]
    public void Checks_apply_over_a_picker_a_plugin_registers([Values] bool raisesEvent)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Eip8288Prototype.Instance))
            .AddScoped<BlockProcessor.IBlockProductionTransactionPicker>(_ => new AlwaysAddPicker(raisesEvent))
            .Build();
        using ILifetimeScope scope = container.BeginLifetimeScope();
        BlockProcessor.IBlockProductionTransactionPicker picker = scope.Resolve<BlockProcessor.IBlockProductionTransactionPicker>();
        List<Transaction> raised = [];
        picker.AddingTransaction += (_, e) => raised.Add(e.Transaction);
        Transaction candidate = DependencyBearingTransaction();
        BlockToProduce block = new(Build.A.BlockHeader.WithGasLimit(10_000_000).TestObject);

        BlockProcessor.AddingTxEventArgs missing = CanAdd(picker, block, candidate);
        container.Resolve<LeanProofStore>().AddVerified(Eip8288Dependencies.ForTransaction(candidate, FrameTxSigHash.ComputeValue), [[1]], null);
        BlockProcessor.AddingTxEventArgs covered = CanAdd(picker, block, candidate);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(picker, Is.InstanceOf<Eip8288TransactionPicker>());
            Assert.That((missing.Action, missing.Reason), Is.EqualTo((BlockProcessor.TxAction.Skip, "Missing verified dependency witnesses")));
            Assert.That(covered.Action, Is.EqualTo(BlockProcessor.TxAction.Add), covered.Reason);
            Assert.That(raised, Is.EqualTo(raisesEvent ? (Transaction[])[candidate] : []));
        }
    }

    // Before EIP-8288 the inner picker's verdict stands: no proof reserve, no dependency check, no per-block state.
    [Test]
    public void Picking_before_eip8288_is_left_to_the_inner_picker([Values] bool nearByteLimit)
    {
        IReleaseSpec spec = Eip8141Prototype.Instance;
        Assert.That(spec.IsEip8288Enabled, Is.False);
        BlockProcessor.IBlockProductionTransactionPicker inner = new BlockProcessor.BlockProductionTransactionPicker(new TestSingleReleaseSpecProvider(spec));
        BlockProcessor.IBlockProductionTransactionPicker picker = CreatePicker(spec);
        List<Transaction> raised = [];
        picker.AddingTransaction += (_, e) => raised.Add(e.Transaction);
        Transaction candidate = DependencyBearingTransaction();
        long txByteLength = nearByteLimit ? BlocksConfig.DefaultMaxTxKilobytes.KiB - Eip8288Constants.MaxAggregateProofBytes : 0;
        BlockToProduce expectedBlock = new(Build.A.BlockHeader.WithGasLimit(10_000_000).TestObject) { TxByteLength = txByteLength };
        BlockToProduce block = new(Build.A.BlockHeader.WithGasLimit(10_000_000).TestObject) { TxByteLength = txByteLength };

        BlockProcessor.AddingTxEventArgs expected = CanAdd(inner, expectedBlock, candidate);
        BlockProcessor.AddingTxEventArgs actual = CanAdd(picker, block, candidate);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected.Action, Is.EqualTo(BlockProcessor.TxAction.Add), expected.Reason);
            Assert.That((actual.Action, actual.Reason), Is.EqualTo((expected.Action, expected.Reason)));
            Assert.That(raised, Is.EqualTo((Transaction[])[candidate]));
            Assert.That(block.LeanProduction.ProofInputs, Is.Empty);
            Assert.That(block.LeanProduction.Budget.Missing([new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default)]), Has.Count.EqualTo(1));
        }
    }

    private static BlockProcessor.AddingTxEventArgs CanAdd(BlockProcessor.IBlockProductionTransactionPicker picker, Block block, Transaction candidate)
    {
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.GetBalance(Arg.Any<Address>()).Returns(UInt256.MaxValue / 2);
        return picker.CanAddTransaction(block, candidate, new HashSet<Transaction>(), state, 0, 0);
    }

    private static Transaction DependencyBearingTransaction()
    {
        Transaction transaction = Build.A.Transaction.WithGasLimit(5_000_000).WithSenderAddress(TestItem.AddressA).TestObject;
        transaction.Type = TxType.FrameTx;
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero,
            Eip8288Dependencies.Serialize([new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default)]))];
        return transaction;
    }

    private sealed class AlwaysAddPicker(bool raisesEvent) : BlockProcessor.IBlockProductionTransactionPicker
    {
        public event EventHandler<BlockProcessor.AddingTxEventArgs>? AddingTransaction;

        public BlockProcessor.AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx,
            IReadOnlySet<Transaction> transactionsInBlock, IReadOnlyStateProvider stateProvider,
            ulong cumulativeBlockExecutionGas, ulong cumulativeBlockStateGas)
        {
            BlockProcessor.AddingTxEventArgs args = new(transactionsInBlock.Count, currentTx, block, transactionsInBlock);
            if (raisesEvent) AddingTransaction?.Invoke(this, args);
            return args;
        }
    }
}
