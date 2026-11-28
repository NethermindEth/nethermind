// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

/// <summary>Building blocks from transactions known only from EIP-8518 proof wrappers.</summary>
public class AggregatedTxProductionTests
{
    private static readonly IReleaseSpec Spec = Eip8288Prototype.Instance;

    [Test]
    public void Production_AggregatedTx_ExtendsTheProofCoveringIt()
    {
        FakeLeanProofVerifier verifier = new();
        DependencyProofCache cache = new(verifier, LimboLogs.Instance);
        Transaction aggregated = Covered(cache, verifier, FrameTxTestFrames.LeanSphincsTx(0, true, (TestItem.KeccakA, TestItem.KeccakB)));
        Transaction raw = FrameTxTestFrames.LeanSphincsTx(1, false, (TestItem.KeccakC, TestItem.KeccakD));

        Block produced = Produce(cache, new AggregatedTxProduction(), deadline: false, aggregated, raw);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(produced.DependencyProof![0], Is.EqualTo(Eip8288Constants.DependencyProofVersionAggregated));
            Assert.That(() => FrameTxDependencyProof.AssignWitnesses(Received(produced), Spec, verifier), Throws.Nothing);
        }
    }

    [Test]
    public void Production_AggregatedTx_UnderADeadline_FailsTheBuildUntilItsProofIsReady()
    {
        FakeLeanProofVerifier verifier = new();
        DependencyProofCache cache = new(verifier, LimboLogs.Instance);
        AggregatedTxProduction production = new();
        Transaction aggregated = Covered(cache, verifier, FrameTxTestFrames.LeanSphincsTx(0, true, (TestItem.KeccakA, TestItem.KeccakB)));
        Transaction raw = FrameTxTestFrames.LeanSphincsTx(1, false, (TestItem.KeccakC, TestItem.KeccakD));

        Assert.That(() => Produce(cache, production, deadline: true, aggregated, raw), Throws.InstanceOf<AggregatedProofNotReadyException>());
        Assert.That(production.ExcludeAggregated, Is.True, "the rebuild leaves the aggregated transaction out");
        Assert.That(() => !cache.IsScheduled && verifier.ProofCalls > 1, Is.True.After(5000, 10));

        Block produced = Produce(cache, production, deadline: true, aggregated, raw);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(production.ExcludeAggregated, Is.False);
            Assert.That(produced.DependencyProof![0], Is.EqualTo(Eip8288Constants.DependencyProofVersionAggregated));
        }
    }

    [Test]
    public void Production_AggregatedTxNoProofCovers_FailsTheBuild()
    {
        FakeLeanProofVerifier verifier = new();
        DependencyProofCache cache = new(verifier, LimboLogs.Instance);
        Transaction aggregated = FrameTxTestFrames.LeanSphincsTx(0, true, (TestItem.KeccakA, TestItem.KeccakB));
        aggregated.ClaimWitnesses![0] = FrameTxClaimVerifier.AggregatedWitness;

        Assert.That(() => Produce(cache, new AggregatedTxProduction(), deadline: false, aggregated), Throws.InstanceOf<AggregatedProofNotReadyException>());
    }

    public enum Case { Covered, NotCovered, Excluded, NotMempoolMode, Raw }

    [TestCase(Case.Covered, BlockProcessor.TxAction.Add)]
    [TestCase(Case.NotCovered, BlockProcessor.TxAction.Skip)]
    [TestCase(Case.Excluded, BlockProcessor.TxAction.Skip)]
    [TestCase(Case.NotMempoolMode, BlockProcessor.TxAction.Skip)]
    [TestCase(Case.Raw, BlockProcessor.TxAction.Add)]
    public void Picker_PicksAnAggregatedTxOnlyWhenAProofCoversIt(Case @case, BlockProcessor.TxAction expected)
    {
        FakeLeanProofVerifier verifier = new();
        DependencyProofCache cache = new(verifier, LimboLogs.Instance);
        Transaction tx = FrameTxTestFrames.LeanSphincsTx(0, true, (TestItem.KeccakA, TestItem.KeccakB));
        if (@case != Case.Raw) tx.ClaimWitnesses![0] = FrameTxClaimVerifier.AggregatedWitness;
        if (@case != Case.NotCovered) Covered(cache, verifier, tx);
        Block block = Build.A.Block.TestObject;
        BlockProcessor.IBlockProductionTransactionPicker inner = Substitute.For<BlockProcessor.IBlockProductionTransactionPicker>();
        inner.CanAddTransaction(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new BlockProcessor.AddingTxEventArgs(0, tx, block, []));
        AggregatedTxPicker picker = new(inner, new TestSingleReleaseSpecProvider(Spec),
            new BlocksConfig { DependencyProofAggregation = @case == Case.NotMempoolMode ? DependencyProofAggregation.BuilderOnly : DependencyProofAggregation.Mempool },
            cache, new AggregatedTxProduction { ExcludeAggregated = @case == Case.Excluded });

        BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(block, tx, new HashSet<Transaction>(), Substitute.For<IReadOnlyStateProvider>(), 0, 0);

        Assert.That(args.Action, Is.EqualTo(expected));
    }

    [Test]
    public void GossipPolicy_KeepsAggregatedTransactionsOutOfAnnouncements()
    {
        Transaction tx = FrameTxTestFrames.LeanSphincsTx(0, true, (TestItem.KeccakA, TestItem.KeccakB));
        AggregatedTxGossipPolicy policy = new();
        bool raw = policy.ShouldGossipTransaction(tx);
        tx.ClaimWitnesses![0] = FrameTxClaimVerifier.AggregatedWitness;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raw, Is.True);
            Assert.That(policy.ShouldGossipTransaction(tx), Is.False);
        }
    }

    /// <summary>Marks <paramref name="tx"/>'s leanSPHINCS witnesses as aggregated and remembers a proof covering them.</summary>
    private static Transaction Covered(DependencyProofCache cache, FakeLeanProofVerifier verifier, Transaction tx)
    {
        List<AggregatedDependency> deps = Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(tx));
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
        cache.Remember(deps, depsHash, verifier.ProveDependencyProof(depsHash, Eip8288Constants.AggregatedVk, new AggregationInput()));
        for (int i = 0; i < tx.FrameClaims!.Length; i++)
        {
            if (Eip8288Dependencies.IsAggregatable(tx.FrameClaims[i].Verifier)) tx.ClaimWitnesses![i] = FrameTxClaimVerifier.AggregatedWitness;
        }

        return tx;
    }

    private static Block Produce(DependencyProofCache cache, AggregatedTxProduction production, bool deadline, params Transaction[] transactions)
    {
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        IBlockProcessor inner = Substitute.For<IBlockProcessor>();
        inner.ProcessOne(default!, default, default!, default!, default).ReturnsForAnyArgs(call =>
        {
            Block processed = call.Arg<Block>();
            FrameTxDependencyProof.Build(processed);
            inner.TransactionsExecuted += Raise.Event<Action<Block>>(processed);
            return (processed, Array.Empty<TxReceipt>());
        });

        AggregatingBlockProcessor processor = new(inner, cache, new BlocksConfig { DependencyProofAggregation = DependencyProofAggregation.Mempool },
            LimboLogs.Instance, Substitute.For<BlockProcessor.IBlockProductionTransactionsExecutor>(), production);
        using CancellationTokenSource cts = new();
        return processor.ProcessOne(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance, Spec, deadline ? cts.Token : CancellationToken.None).Block;
    }

    /// <summary>The block as a validating node receives it, without the producer's witnesses.</summary>
    private static Block Received(Block produced)
    {
        Transaction[] transactions = new Transaction[produced.Transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = new Transaction();
            produced.Transactions[i].CopyTo(transactions[i]);
            transactions[i].ClaimWitnesses = null;
        }

        Block received = Build.A.Block.WithTransactions(transactions).TestObject;
        received.DependencyProof = produced.DependencyProof;
        received.Header.DependencyProofHash = produced.Header.DependencyProofHash;
        return received;
    }
}
