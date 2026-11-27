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
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

/// <summary>The EIP-8288 aggregated dependency proof: its encoding, its checks on a received block, and its production
/// under each <see cref="DependencyProofAggregation"/> mode.</summary>
public class AggregatedDependencyProofTests
{
    private static readonly IReleaseSpec Spec = Eip8288Prototype.Instance;

    [Test]
    public void Encode_IsTheAggregatedVersionByteThenTheStarkAndTheRawWitnesses()
    {
        byte[] proof = AggregatedDependencyProof.Encode([7, 7], [[1, 2, 3], []]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof[0], Is.EqualTo(Eip8288Constants.DependencyProofVersionAggregated));
            // [0x0707, [0x010203, 0x]]: 0xc9, 0x82 0707, 0xc5 (0x83 010203, 0x80).
            Assert.That(proof[1..], Is.EqualTo(new byte[] { 0xc9, 0x82, 0x07, 0x07, 0xc5, 0x83, 0x01, 0x02, 0x03, 0x80 }));
            Assert.That(AggregatedDependencyProof.TryDecode(proof, out byte[] stark, out byte[][] witnesses), Is.True);
            Assert.That(stark, Is.EqualTo(new byte[] { 7, 7 }));
            Assert.That(witnesses, Is.EqualTo(new byte[][] { [1, 2, 3], [] }));
        }
    }

    /// <summary>The leanSPHINCS claims take the aggregate's place, and every other claim keeps its raw witness.</summary>
    [Test]
    public void AssignWitnesses_AggregatedProof_StandsInForTheLeanSphincsClaimsOnly()
    {
        FakeLeanProofVerifier verifier = new();
        (Block received, Transaction mixed) = ReceivedAggregated(verifier);

        FrameTxDependencyProof.AssignWitnesses(received, Spec, verifier);

        byte[][] witnesses = received.Transactions[0].ClaimWitnesses!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(witnesses[0], Is.SameAs(FrameTxClaimVerifier.AggregatedWitness));
            Assert.That(witnesses[1], Is.EqualTo(mixed.ClaimWitnesses![1]));
            Assert.That(witnesses[2], Is.SameAs(FrameTxClaimVerifier.AggregatedWitness));
            Assert.That(FrameTxClaimVerifier.CheckWitnesses(received.Transactions[0], false, out _, acceptAggregated: true), Is.True);
            Assert.That(FrameTxClaimVerifier.CheckWitnesses(received.Transactions[0], false, out _), Is.False,
                "only block execution accepts the aggregate's stand-in, so a reorged transaction is not admitted to the pool on it");
        }
    }

    public enum Defect { StarkRejected, ForkWithoutEip8288, WitnessMissing, NothingAggregated, Malformed }

    [TestCase(Defect.StarkRejected, AggregatedDependencyProof.InvalidStark)]
    [TestCase(Defect.ForkWithoutEip8288, FrameTxDependencyProof.Malformed)]
    [TestCase(Defect.WitnessMissing, FrameTxDependencyProof.WitnessCountMismatch)]
    [TestCase(Defect.NothingAggregated, AggregatedDependencyProof.NothingAggregated)]
    [TestCase(Defect.Malformed, AggregatedDependencyProof.Malformed)]
    public void AssignWitnesses_AggregatedProofThatDoesNotFit_InvalidatesTheBlock(Defect defect, string expectedError)
    {
        FakeLeanProofVerifier verifier = new();
        (Block received, _) = ReceivedAggregated(verifier);
        IReleaseSpec spec = defect == Defect.ForkWithoutEip8288 ? Eip8141Prototype.Instance : Spec;
        byte[]? proof = defect switch
        {
            Defect.WitnessMissing => AggregatedDependencyProof.Encode(Stark(received), []),
            Defect.NothingAggregated => AggregatedDependencyProof.Encode([1], [new byte[Eip8141Constants.Secp256k1WitnessLength]]),
            Defect.Malformed => [Eip8288Constants.DependencyProofVersionAggregated, 0xc0],
            _ => null
        };
        if (defect == Defect.NothingAggregated)
        {
            Transaction secpOnly = FrameTxWith(TestItem.AddressA, FrameTxTestFrames.Secp256k1Claim(null));
            received = Build.A.Block.WithTransactions(secpOnly).TestObject;
        }

        if (proof is not null) Commit(received, proof);
        verifier.Reject = defect == Defect.StarkRejected;

        Assert.That(() => FrameTxDependencyProof.AssignWitnesses(received, spec, verifier),
            Throws.InstanceOf<InvalidBlockException>().With.Message.Contains(expectedError));
    }

    /// <summary>A verifier that fails rather than reject reaches no verdict, so the block is not marked invalid.</summary>
    [Test]
    public void AssignWitnesses_VerifierFault_ReachesNoVerdict()
    {
        FakeLeanProofVerifier verifier = new() { Fault = true };
        (Block received, _) = ReceivedAggregated(verifier);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => FrameTxDependencyProof.AssignWitnesses(received, Spec, verifier), Throws.Exception.Not.InstanceOf<InvalidBlockException>());
            Assert.That(() => FrameTxDependencyProof.AssignWitnesses(received, Spec, leanProofVerifier: null), Throws.Exception.Not.InstanceOf<InvalidBlockException>());
        }
    }

    [Test]
    public void Production_None_KeepsTheRawWitnesses()
    {
        FakeLeanProofVerifier verifier = new();
        (Block produced, byte[] raw) = Produce(DependencyProofAggregation.None, verifier, deadline: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(produced.DependencyProof, Is.EqualTo(raw));
            Assert.That(produced.DependencyProof![0], Is.EqualTo(Eip8141Constants.DependencyProofVersionRaw));
            Assert.That(verifier.ProofCalls, Is.Zero);
        }
    }

    [Test]
    public void Production_BuilderOnly_AggregatesAProofAReceivingNodeAccepts()
    {
        FakeLeanProofVerifier verifier = new();
        (Block produced, byte[] raw) = Produce(DependencyProofAggregation.BuilderOnly, verifier, deadline: false);
        Block received = ReceivedCopy(produced);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(produced.DependencyProof![0], Is.EqualTo(Eip8288Constants.DependencyProofVersionAggregated));
            Assert.That(produced.DependencyProof, Has.Length.LessThan(raw.Length));
            Assert.That(produced.Header.DependencyProofHash, Is.EqualTo(Keccak.Compute(produced.DependencyProof)));
            Assert.That(() => FrameTxDependencyProof.AssignWitnesses(received, Spec, verifier), Throws.Nothing);
        }
    }

    /// <summary>A build under a deadline never waits for the prover: it ships raw witnesses, and a later build of the same
    /// dependencies uses the proof proven meanwhile.</summary>
    [Test]
    public void Production_BuilderOnly_UnderADeadline_ShipsRawUntilTheProofIsReady()
    {
        FakeLeanProofVerifier verifier = new();
        DependencyProofCache cache = new(verifier, LimboLogs.Instance);
        (Block first, byte[] raw) = Produce(DependencyProofAggregation.BuilderOnly, verifier, deadline: true, cache);
        Assert.That(() => !cache.IsScheduled && verifier.ProofCalls > 0, Is.True.After(5000, 10));
        (Block second, _) = Produce(DependencyProofAggregation.BuilderOnly, verifier, deadline: true, cache);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.DependencyProof, Is.EqualTo(raw));
            Assert.That(second.DependencyProof![0], Is.EqualTo(Eip8288Constants.DependencyProofVersionAggregated));
        }
    }

    [Test]
    public void Production_BuilderOnly_KeepsRawWitnessesWhenTheStarkIsLarger()
    {
        FakeLeanProofVerifier verifier = new() { ProofLength = 3 * Eip8141Constants.LeanSphincsSignatureSize };
        (Block produced, byte[] raw) = Produce(DependencyProofAggregation.BuilderOnly, verifier, deadline: false);

        Assert.That(produced.DependencyProof, Is.EqualTo(raw));
    }

    [Test]
    public void Prove_FoldsMoreWitnessesThanOneLeafTakes()
    {
        FakeLeanProofVerifier verifier = new();
        List<AggregatedDependency> deps = [];
        List<ReadOnlyMemory<byte>> witnesses = [];
        for (int i = 0; i < 3 * DependencyProofAggregator.DirectBatchSize + 1; i++)
        {
            deps.Add(new AggregatedDependency(Keccak.Compute([(byte)i]).ValueHash256, TestItem.KeccakA.ValueHash256));
            witnesses.Add(new byte[Eip8288Constants.LeanSphincsWitnessBytes]);
        }

        List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(canonical);
        byte[] proof = DependencyProofAggregator.Prove(DependencyProofAggregator.Combine(deps, witnesses, canonical), verifier, depsHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.VerifyDependencyProof(depsHash, Eip8288Constants.AggregatedVk, proof), Is.True);
            Assert.That(verifier.LargestRawInput, Is.LessThanOrEqualTo(DependencyProofAggregator.DirectBatchSize));
            Assert.That(verifier.ProofCalls, Is.GreaterThan(4), "four leaves and the merges above them");
        }
    }

    /// <summary>Produces a block of one transaction with two leanSPHINCS claims around a secp256k1 one.</summary>
    private static (Block Produced, byte[] Raw) Produce(DependencyProofAggregation mode, FakeLeanProofVerifier verifier, bool deadline,
        DependencyProofCache? cache = null)
    {
        Block block = Build.A.Block.WithTransactions(MixedTx()).TestObject;
        byte[] raw = [];
        IBlockProcessor inner = Substitute.For<IBlockProcessor>();
        inner.ProcessOne(default!, default, default!, default!, default).ReturnsForAnyArgs(call =>
        {
            Block processed = call.Arg<Block>();
            FrameTxDependencyProof.Build(processed);
            raw = processed.DependencyProof!;
            inner.TransactionsExecuted += Raise.Event<Action<Block>>(processed);
            return (processed, Array.Empty<TxReceipt>());
        });

        AggregatingBlockProcessor processor = new(inner, Substitute.For<BlockProcessor.IBlockProductionTransactionsExecutor>(),
            cache ?? new DependencyProofCache(verifier, LimboLogs.Instance), new BlocksConfig { DependencyProofAggregation = mode }, LimboLogs.Instance);
        using CancellationTokenSource cts = new();
        (Block produced, _) = processor.ProcessOne(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance, Spec, deadline ? cts.Token : CancellationToken.None);
        return (produced, raw);
    }

    private static (Block Received, Transaction Mixed) ReceivedAggregated(FakeLeanProofVerifier verifier)
    {
        Transaction mixed = MixedTx();
        Block produced = Build.A.Block.WithTransactions(mixed).TestObject;
        FrameTxDependencyProof.Build(produced);
        Block received = ReceivedCopy(produced);
        Commit(received, AggregatedDependencyProof.Encode(Stark(received), [mixed.ClaimWitnesses![1]]));
        return (received, mixed);
    }

    private static byte[] Stark(Block block)
    {
        List<AggregatedDependency> aggregated = AggregatedDependencyProof.Aggregated(FrameTxDependencyProof.BlockDependencies(block));
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(aggregated);
        return new FakeLeanProofVerifier().ProveDependencyProof(depsHash, Eip8288Constants.AggregatedVk, new AggregationInput());
    }

    private static void Commit(Block block, byte[] proof)
    {
        block.DependencyProof = proof;
        block.Header.DependencyProofHash = Keccak.Compute(proof);
    }

    private static Transaction MixedTx()
    {
        TxFrameClaim first = LeanSphincsClaim(TestItem.KeccakA, TestItem.KeccakB);
        TxFrameClaim second = LeanSphincsClaim(TestItem.KeccakC, TestItem.KeccakA);
        Transaction tx = FrameTxWith(TestItem.AddressA, first, FrameTxTestFrames.Secp256k1Claim(null), second);
        tx.ClaimWitnesses = [Filled(Eip8141Constants.LeanSphincsSignatureSize, 1), Filled(Eip8141Constants.Secp256k1WitnessLength, 2), Filled(Eip8141Constants.LeanSphincsSignatureSize, 3)];
        return tx;
    }

    private static TxFrameClaim LeanSphincsClaim(Hash256 publicKey, Hash256 message) =>
        new(Eip8141Constants.LeanSphincsVerifierAddress, publicKey.BytesToArray(), message.BytesToArray(), 0, 0);

    private static Transaction FrameTxWith(Address sender, params TxFrameClaim[] claims)
    {
        Transaction tx = FrameTxTestFrames.FrameTx(sender, claims, FrameTxTestFrames.SelfVerify());
        tx.ChainId = TestBlockchainIds.ChainId;
        return tx;
    }

    /// <summary>The block as a validating node receives it: the same bodies, without the producer's witnesses.</summary>
    private static Block ReceivedCopy(Block produced)
    {
        Transaction[] transactions = new Transaction[produced.Transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = new Transaction();
            produced.Transactions[i].CopyTo(transactions[i]);
            transactions[i].ClaimWitnesses = null;
        }

        Block received = Build.A.Block.WithTransactions(transactions).TestObject;
        Commit(received, produced.DependencyProof!);
        return received;
    }

    private static byte[] Filled(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
