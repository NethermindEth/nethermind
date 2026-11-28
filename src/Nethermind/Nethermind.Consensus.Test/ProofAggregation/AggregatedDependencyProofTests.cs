// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Threading;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
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

    public enum Defect { StarkRejected, ForkWithoutEip8288, WitnessMissing, NothingAggregated, Malformed, EmptyProof }

    [TestCase(Defect.StarkRejected, AggregatedDependencyProof.InvalidStark)]
    [TestCase(Defect.ForkWithoutEip8288, FrameTxDependencyProof.Malformed)]
    [TestCase(Defect.WitnessMissing, FrameTxDependencyProof.WitnessCountMismatch)]
    [TestCase(Defect.NothingAggregated, AggregatedDependencyProof.NothingAggregated)]
    [TestCase(Defect.Malformed, AggregatedDependencyProof.Malformed)]
    // A header committing to the empty proof over a body with claims, as a synced block defaults to.
    [TestCase(Defect.EmptyProof, FrameTxDependencyProof.Malformed)]
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
            Defect.EmptyProof => [],
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

    /// <summary>A builder's deadline-bound production uses a proof proven off the deadline, and the node's validation processor verifies it
    /// through the node's verifier, on either access list execution path.</summary>
    [Test]
    public async Task BlockProcessor_ProducesAndProcessesAnAggregatedBlock([Values] bool parallelExecution)
    {
        Address claimant = TestItem.AddressD;
        FakeLeanProofVerifier verifier = new();
        DependencyProofCache cache = new(verifier, LimboLogs.Instance);
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Spec) { AllowTestChainOverride = false })
            .Intercept<IBlocksConfig>(blocksConfig =>
            {
                blocksConfig.ParallelExecution = parallelExecution;
                blocksConfig.DependencyProofAggregation = DependencyProofAggregation.BuilderOnly;
            })
            .AddSingleton(cache)
            .AddSingleton<ILeanSphincsVerifier>(verifier)
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(claimant, 100.Ether);
                state.InsertCode(claimant, Prepare.EvmCode
                    .PushData((byte)FrameFlags.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done, specProvider.GenesisSpec);
            }));

        Transaction tx = FrameTxWith(claimant, LeanSphincsClaim(TestItem.KeccakA, TestItem.KeccakB), LeanSphincsClaim(TestItem.KeccakC, TestItem.KeccakA));
        tx.GasPrice = tx.DecodedMaxFeePerGas = 1.GWei;
        tx.GasLimit = 1_000_000;
        tx.ClaimWitnesses = [Filled(Eip8141Constants.LeanSphincsSignatureSize, 1), Filled(Eip8141Constants.LeanSphincsSignatureSize, 3)];
        tx.Hash = tx.CalculateHash();
        // Proven off the payload deadline, as a scheduled statement is, so the deadline-bound build uses it.
        List<AggregatedDependency> deps = Eip8288Dependencies.Canonicalize(
            AggregatedDependencyProof.Aggregated(FrameTxDependencyProof.BlockDependencies(Build.A.Block.WithTransactions(tx).TestObject)));
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
        cache.Remember(deps, depsHash, verifier.ProveDependencyProof(depsHash, Eip8288Constants.AggregatedVk, new AggregationInput()));

        Assert.That(chain.TxPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        chain.Timestamper.Add(TimeSpan.FromSeconds(12));
        using CancellationTokenSource deadline = new();
        Block produced = (await chain.BlockProducer.BuildBlock(parent, cancellationToken: deadline.Token))!;
        Assert.That(produced.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { tx.Hash }), "precondition: the claimant is included");

        // Replayed as received, without the producer's witnesses, on the path the configuration selects.
        Block received = ReceivedCopy(produced);
        received.BlockAccessList = Rlp.Decode<ReadOnlyBlockAccessList>(produced.EncodedBlockAccessList!);
        IBlockAccessListManager balManager = ((MainProcessingContext)chain.MainProcessingContext).LifetimeScope.Resolve<IBlockAccessListManager>();
        TxReceipt[] receipts;
        using (chain.MainWorldState.BeginScope(parent))
        {
            (_, receipts) = chain.BlockProcessor.ProcessOne(received, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(produced.Header), CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(balManager.ParallelExecutionEnabled, Is.EqualTo(parallelExecution), "precondition: the block runs on the path the configuration selects");
            Assert.That(produced.DependencyProof![0], Is.EqualTo(Eip8288Constants.DependencyProofVersionAggregated));
            Assert.That(received.Transactions[0].ClaimWitnesses, Has.All.SameAs(FrameTxClaimVerifier.AggregatedWitness));
            Assert.That(receipts.Single().StatusCode, Is.EqualTo(TxFrameReceipt.StatusSuccess));
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

    /// <summary>A payload built once over a quiet pool ships raw witnesses, and is rebuilt with the aggregated proof
    /// once the proof its build scheduled is ready.</summary>
    [Test]
    [CancelAfter(30000)]
    public async Task Production_BuilderOnly_QuietPool_GetPayloadCarriesTheProofOnceReady()
    {
        using ManualResetEventSlim provingGate = new();
        FakeLeanProofVerifier verifier = new() { ProvingGate = provingGate };
        DependencyProofCache cache = new(verifier, LimboLogs.Instance);
        AggregatingProducer producer = new(verifier, cache);
        TimeSpan slot = TimeSpan.FromSeconds(12);
        CapturingImprovementContextFactory factory = new(new AggregatingBlockImprovementContextFactory(
            new BlockImprovementContextFactory(producer, slot), cache,
            new BlocksConfig { DependencyProofAggregation = DependencyProofAggregation.BuilderOnly }, new TestSpecProvider(Spec)));
        using PayloadPreparationService service = new(producer, Substitute.For<ITxPool>(), factory, Substitute.For<ITimerFactory>(), LimboLogs.Instance, slot);

        string payloadId = service.StartPreparingPayload(Build.A.BlockHeader.TestObject, new PayloadAttributes { Timestamp = 100 });
        Assert.That(() => factory.Last?.ImprovementTask.IsCompleted == true && cache.IsScheduled, Is.True.After(5000, 10));
        byte[] first = factory.Last!.Best.CurrentBestBlock!.DependencyProof!;
        provingGate.Set();
        Assert.That(() => factory.Last.Best.CurrentBestBlock!.DependencyProof![0], Is.EqualTo(Eip8288Constants.DependencyProofVersionAggregated).After(5000, 10));
        IBlockProductionContext? payload = await service.GetPayload(payloadId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first[0], Is.EqualTo(Eip8141Constants.DependencyProofVersionRaw), "precondition: the first build ships raw witnesses");
            Assert.That(payload!.CurrentBestBlock!.DependencyProof![0], Is.EqualTo(Eip8288Constants.DependencyProofVersionAggregated));
            Assert.That(producer.Builds, Is.EqualTo(2), "one build, and one rebuild once the proof is ready");
        }
    }

    [Test]
    public void Production_BuilderOnly_KeepsRawWitnessesWhenTheProverFails()
    {
        FakeLeanProofVerifier verifier = new() { Fault = true };
        (Block produced, byte[] raw) = Produce(DependencyProofAggregation.BuilderOnly, verifier, deadline: false);

        Assert.That(produced.DependencyProof, Is.EqualTo(raw));
    }

    [Test]
    public void Production_BuilderOnly_KeepsRawWitnessesWhenTheStarkIsLarger()
    {
        FakeLeanProofVerifier verifier = new() { ProofLength = 3 * Eip8141Constants.LeanSphincsSignatureSize };
        (Block produced, byte[] raw) = Produce(DependencyProofAggregation.BuilderOnly, verifier, deadline: false);

        Assert.That(produced.DependencyProof, Is.EqualTo(raw));
    }

    /// <summary>Signatures fold in the fewest equal leaves within the native bound, and recursive inputs merge
    /// <see cref="DependencyProofAggregator.FanIn"/> at a time in calls of their own.</summary>
    [TestCase(1, 0, new[] { 1 }, 1)]
    [TestCase(16, 0, new[] { 16 }, 1)]
    [TestCase(256, 0, new[] { 256 }, 1)]
    [TestCase(257, 0, new[] { 129, 128 }, 3)]
    [TestCase(500, 0, new[] { 250, 250 }, 3)]
    [TestCase(500, 3, new[] { 250, 250 }, 3)]
    [TestCase(17, 9, new[] { 17 }, 4)]
    [TestCase(20, 2, new[] { 20 }, 1)]
    [TestCase(20, 3, new[] { 20 }, 2)]
    [TestCase(20, 5, new[] { 20 }, 2)]
    public void Prove_FoldsSignaturesInTheFewestEqualLeaves(int signatures, int recursive, int[] leaves, int calls)
    {
        FakeLeanProofVerifier verifier = new();
        List<AggregatedDependency> deps = [];
        List<ReadOnlyMemory<byte>> witnesses = [];
        for (int i = 0; i < signatures; i++)
        {
            deps.Add(Eip8288Dependencies.Map(TestItem.KeccakA.ValueHash256, Keccak.Compute(BitConverter.GetBytes(i)).ValueHash256));
            witnesses.Add(new byte[Eip8288Constants.LeanSphincsWitnessBytes]);
        }

        List<RecursiveProofInput> children = [];
        for (int i = 0; i < recursive; i++)
        {
            List<AggregatedDependency> inner = [Eip8288Dependencies.Map(TestItem.KeccakB.ValueHash256, Keccak.Compute(BitConverter.GetBytes(-1 - i)).ValueHash256)];
            children.Add(new RecursiveProofInput(inner, verifier.ProveDependencyProof(Eip8288Dependencies.ComputeDepsHash(inner), Eip8288Constants.AggregatedVk, new AggregationInput())));
        }

        verifier.ProvingInputs.Clear();
        List<AggregatedDependency> all = [.. deps];
        foreach (RecursiveProofInput child in children) all.AddRange(child.InnerDeps);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.Canonicalize(all));
        byte[] proof = DependencyProofAggregator.Prove(new AggregationInput { Deps = deps, Witnesses = witnesses, RecursiveProofs = children }, verifier, depsHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.VerifyDependencyProof(depsHash, Eip8288Constants.AggregatedVk, proof), Is.True);
            Assert.That(verifier.ProvingInputs.Where(static i => i.Deps.Count != 0).Select(static i => i.Deps.Count), Is.EqualTo(leaves));
            Assert.That(verifier.ProvingInputs, Has.Count.EqualTo(calls), "native calls, up to two recursive inputs folding into a leaf's own");
            Assert.That(verifier.ProvingInputs.Select(static i => i.RecursiveProofs.Count), Has.All.LessThanOrEqualTo(DependencyProofAggregator.FanIn));
            Assert.That(verifier.ProvingInputs.Where(static i => i.Deps.Count != 0).Select(static i => i.RecursiveProofs.Count),
                Has.All.LessThanOrEqualTo(2), "a leaf never shares a call with a wide merge");
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

        AggregatingBlockProcessor processor = new(inner, cache ?? new DependencyProofCache(verifier, LimboLogs.Instance),
            new BlocksConfig { DependencyProofAggregation = mode }, LimboLogs.Instance, Substitute.For<BlockProcessor.IBlockProductionTransactionsExecutor>());
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

        Block received = new(produced.Header.Clone(), new BlockBody(transactions, [], produced.Withdrawals));
        Commit(received, produced.DependencyProof!);
        return received;
    }

    /// <summary>Builds every payload as <see cref="Produce"/> does, used enough gas to improve on the empty block.</summary>
    private sealed class AggregatingProducer(FakeLeanProofVerifier verifier, DependencyProofCache cache) : IBlockProducer
    {
        private int _builds;

        public int Builds => Volatile.Read(ref _builds);

        public Task<Block?> BuildBlock(BlockHeader? parentHeader = null, IBlockTracer? blockTracer = null, PayloadAttributes? payloadAttributes = null,
            IBlockProducer.Flags flags = IBlockProducer.Flags.None, CancellationToken cancellationToken = default)
        {
            if (flags.HasFlag(IBlockProducer.Flags.PrepareEmptyBlock)) return Task.FromResult<Block?>(Build.A.Block.TestObject);

            Interlocked.Increment(ref _builds);
            (Block produced, _) = Produce(DependencyProofAggregation.BuilderOnly, verifier, cancellationToken.CanBeCanceled, cache);
            produced.Header.GasUsed = 21_000;
            return Task.FromResult<Block?>(produced);
        }
    }

    private sealed class CapturingImprovementContextFactory(IBlockImprovementContextFactory inner) : IBlockImprovementContextFactory
    {
        private IBlockImprovementContext? _last;

        public IBlockImprovementContext? Last => Volatile.Read(ref _last);

        public IBlockImprovementContext StartBlockImprovementContext(Block? currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes,
            DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts) =>
            _last = inner.StartBlockImprovementContext(currentBestBlock, parentHeader, payloadAttributes, startDateTime, currentBlockFees, cts);
    }

    private static byte[] Filled(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
