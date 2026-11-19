// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Blockchain.Tracing;
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
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
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

    // A contribution is committed once the executor's verdict on its candidate is final: the last transaction's on the read
    // that proof attachment makes, and none for a transaction that fails. Processing works on a copy of the suggested block,
    // so a rebuild or retry from that block starts with no committed contributions.
    [Test]
    public void Contributions_of_included_transactions_are_committed_through_the_last_one([Values] bool lastFails)
    {
        FrameDependency first = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        FrameDependency last = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakB.ValueHash256, default);
        Transaction firstTx = DependencyBearingTransaction(first);
        Transaction lastTx = DependencyBearingTransaction(last);
        LeanProofStore proofs = new(FrameTxSigHash.ComputeValue);
        proofs.AddVerified([first], [[1]], null);
        proofs.AddVerified([last], [[2]], null);

        (BlockToProduce suggested, BlockToProduce processed) = Produce(proofs, lastFails ? lastTx : null, firstTx, lastTx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed.Transactions, Is.EqualTo(lastFails ? (Transaction[])[firstTx] : [firstTx, lastTx]));
            Assert.That(processed.LeanProduction.ProofInputs.Select(static input => input.Deps.Single()),
                Is.EqualTo(lastFails ? (FrameDependency[])[first] : [first, last]));
            Assert.That(processed.LeanProduction.Budget.Missing([first, last]), Is.EqualTo(lastFails ? (FrameDependency[])[last] : []));
            Assert.That(suggested.LeanProduction.ProofInputs, Is.Empty);
            Assert.That(suggested.LeanProduction.Budget.Missing([first, last]), Has.Count.EqualTo(2));
        }
    }

    // Each contribution must see the one committed before it, or a shared dependency is fetched and charged twice.
    [Test]
    public void A_dependency_shared_by_consecutive_transactions_is_charged_once()
    {
        FrameDependency shared = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        Transaction firstTx = DependencyBearingTransaction(shared, TestItem.KeccakC);
        Transaction secondTx = DependencyBearingTransaction(shared, TestItem.KeccakD);
        LeanProofStore proofs = new(FrameTxSigHash.ComputeValue);
        proofs.AddVerified([shared], [[1]], null);

        (_, BlockToProduce processed) = Produce(proofs, null, firstTx, secondTx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed.Transactions, Is.EqualTo((Transaction[])[firstTx, secondTx]));
            Assert.That(processed.LeanProduction.ProofInputs, Has.Count.EqualTo(1));
            Assert.That(processed.LeanProduction.ProofInputs[0].Deps, Is.EqualTo((FrameDependency[])[shared]));
        }
    }

    /// <summary>Runs the production executor with the decorated picker over a copy of a block of <paramref name="transactions"/>,
    /// as block processing does, failing the execution of <paramref name="failing"/>.</summary>
    private static (BlockToProduce Suggested, BlockToProduce Processed) Produce(LeanProofStore proofs, Transaction? failing,
        params Transaction[] transactions)
    {
        ITransactionProcessorAdapter adapter = Substitute.For<ITransactionProcessorAdapter>();
        adapter.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(call =>
        {
            if (call.Arg<Transaction>() == failing) return TransactionResult.ErrorType.InsufficientSenderBalance;
            call.Arg<ITxTracer>().MarkAsSuccess(Address.Zero, new GasConsumed(GasCostOf.Transaction, GasCostOf.Transaction), [], []);
            return TransactionResult.Ok;
        });
        BlockProcessor.BlockProductionTransactionsExecutor executor = new(adapter, Substitute.For<IWorldState>(),
            CreatePicker(Eip8288Prototype.Instance, proofs), LimboLogs.Instance, NullBlockAccessListManager.Instance, NullTxPool.Instance);
        BlockToProduce suggested = new(Build.A.BlockHeader.WithGasLimit(10_000_000).TestObject, transactions, []);
        BlockToProduce processed = (BlockToProduce)suggested.WithReplacedHeader(suggested.Header.Clone());
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(processed);
        executor.SetBlockExecutionContext(new BlockExecutionContext(processed.Header, Eip8288Prototype.Instance));
        executor.ProcessTransactions(processed, ProcessingOptions.ProducingBlock, receiptsTracer);
        return (suggested, processed);
    }

    private static BlockProcessor.AddingTxEventArgs CanAdd(BlockProcessor.IBlockProductionTransactionPicker picker, Block block, Transaction candidate)
    {
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.GetBalance(Arg.Any<Address>()).Returns(UInt256.MaxValue / 2);
        return picker.CanAddTransaction(block, candidate, new HashSet<Transaction>(), state, 0, 0);
    }

    private static Transaction DependencyBearingTransaction(FrameDependency? dependency = null, Hash256? hash = null)
    {
        FrameDependency verified = dependency ?? new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        Transaction transaction = Build.A.Transaction.WithGasLimit(5_000_000).WithSenderAddress(TestItem.AddressA)
            .WithHash(hash ?? Keccak.Compute(verified.DataHash.Bytes)).TestObject;
        transaction.Type = TxType.FrameTx;
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero,
            Eip8288Dependencies.Serialize([verified]))];
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
