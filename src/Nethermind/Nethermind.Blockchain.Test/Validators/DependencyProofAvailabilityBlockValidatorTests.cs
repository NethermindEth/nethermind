// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Blocks;
using Nethermind.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

[Parallelizable(ParallelScope.All)]
public class DependencyProofAvailabilityBlockValidatorTests
{
    private static readonly byte[] Proof = [1, 2, 3];

    // A block read back from the store, as one inserted by newPayload while syncing is, is validated with its stored proof.
    [Test]
    public void Validates_a_block_with_the_proof_the_store_holds()
    {
        (Block block, DependencyProofStore store) = Committed(10);
        store.Insert(block.Number, block.Hash!, Proof);
        RecordingValidator inner = new();

        bool valid = Create(inner, store).ValidateSuggestedBlock(block, Build.A.BlockHeader.TestObject, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.True, error);
            Assert.That(inner.ValidatedProof, Is.EqualTo(Proof));
            Assert.That(block.StarkProof, Is.Null, "the caller's block, which may be a cached instance, does not gain the proof");
        }
    }

    // EIPs#12549 and EIP-8498: a missing proof is accepted only for the finalized block or a canonical ancestor of it, and
    // only that failure is. A side-chain block below the finalized height is not trusted.
    [TestCase("none", BlockErrorMessages.MissingDependencyProof, false)]
    [TestCase("below", BlockErrorMessages.MissingDependencyProof, false)]
    [TestCase("finalized", BlockErrorMessages.MissingDependencyProof, true)]
    [TestCase("canonical-ancestor", BlockErrorMessages.MissingDependencyProof, true)]
    [TestCase("beacon-ancestor", BlockErrorMessages.MissingDependencyProof, true)]
    [TestCase("side-chain", BlockErrorMessages.MissingDependencyProof, false)]
    [TestCase("unmarked", BlockErrorMessages.MissingDependencyProof, false)]
    [TestCase("canonical-off-beacon", BlockErrorMessages.MissingDependencyProof, false)]
    [TestCase("beacon-off-canonical", BlockErrorMessages.MissingDependencyProof, false)]
    [TestCase("canonical-ancestor", BlockErrorMessages.InvalidRecursiveStark, false)]
    public void Trusts_finality_only_for_a_missing_proof_of_a_finalized_ancestor(string relation, string innerError, bool expected)
    {
        (Block block, DependencyProofStore store) = Committed(10);
        RecordingValidator inner = new() { Error = innerError };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader? finalized = relation switch
        {
            "none" => null,
            "below" => Build.A.BlockHeader.WithNumber(9).TestObject,
            "finalized" => block.Header,
            _ => Build.A.BlockHeader.WithNumber(11).TestObject
        };
        blockTree.FindFinalizedHeader().Returns(finalized);
        BlockInfo levelBlock = new(relation == "side-chain" ? TestItem.KeccakH : block.Hash!, 0,
            relation == "beacon-ancestor" ? BlockMetadata.BeaconMainChain : BlockMetadata.None);
        // canonical-off-beacon: the block is canonical, but beacon sync marks another chain at its height.
        // beacon-off-canonical: beacon sync marks the block, but another block is canonical at its height.
        BlockInfo[] levelBlocks = relation switch
        {
            "canonical-off-beacon" => [levelBlock, new BlockInfo(TestItem.KeccakG, 0, BlockMetadata.BeaconMainChain)],
            "beacon-off-canonical" => [new BlockInfo(TestItem.KeccakG, 0), new BlockInfo(block.Hash!, 0, BlockMetadata.BeaconMainChain)],
            _ => [levelBlock]
        };
        bool hasMainChain = relation is "canonical-ancestor" or "side-chain" or "canonical-off-beacon" or "beacon-off-canonical";
        blockTree.FindLevel(block.Number).Returns(new ChainLevelInfo(hasMainChain, levelBlocks));

        bool valid = new DependencyProofAvailabilityBlockValidator(inner, store, blockTree)
            .ValidateSuggestedBlock(block, Build.A.BlockHeader.TestObject, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(expected));
            Assert.That(error, expected ? Is.Null : Is.EqualTo(innerError));
        }
    }

    // The proof store join wraps the dependency proof check in the module, so the proof verified is the stored one.
    [Test]
    public void Module_verifies_the_stored_proof_of_a_block_received_without_it([Values] bool verdict)
    {
        Block block = Build.A.Block.WithNumber(10).WithTransactions(DependencyTx()).TestObject;
        block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(Proof),
            new Hash256(Eip8288Dependencies.ComputeBlockDepsHash(block, FrameTxSigHash.ComputeValue)));
        block.Header.Hash = block.Header.CalculateHash();
        DependencyProofStore store = new(new TestMemDb());
        store.Insert(block.Number, block.Hash!, Proof);
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8288Enabled.Returns(true);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        ProofRecordingVerifier verifier = new(verdict);

        ContainerBuilder builder = new();
        builder.RegisterModule(new BlockProcessingModule(new InitConfig(), new BlocksConfig()));
        builder.RegisterInstance(new RecordingValidator()).As<IBlockValidator>();
        builder.RegisterInstance(specProvider).As<ISpecProvider>();
        builder.RegisterInstance(LimboLogs.Instance).As<ILogManager>();
        builder.RegisterInstance(store).As<IDependencyProofStore>();
        builder.RegisterInstance(Substitute.For<IBlockTree>()).As<IBlockTree>();
        builder.RegisterInstance(verifier).As<ILeanProofVerifier>();
        using IContainer container = builder.Build();

        bool valid = container.Resolve<IBlockValidator>().ValidateSuggestedBlock(block, Build.A.BlockHeader.TestObject, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.EqualTo(verdict), error);
            Assert.That(error, Is.EqualTo(verdict ? null : BlockErrorMessages.InvalidRecursiveStark));
            Assert.That(verifier.VerifiedProof, Is.EqualTo(Proof));
        }
    }

    private static Transaction DependencyTx()
    {
        byte[] depData = new byte[Eip8288Constants.DependencyTripleLength];
        depData[31] = Eip8288Constants.LeanSphincsScheme;
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(FrameMode.DepVerify, 0, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, depData)],
            FrameSignatures = [],
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static DependencyProofAvailabilityBlockValidator Create(IBlockValidator inner, IDependencyProofStore store) =>
        new(inner, store, Substitute.For<IBlockTree>());

    private static (Block Block, DependencyProofStore Store) Committed(ulong number)
    {
        Block block = Build.A.Block.WithNumber(number).TestObject;
        block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(Proof), Keccak.Compute("deps"));
        block.Header.Hash = block.Header.CalculateHash();
        return (block, new DependencyProofStore(new TestMemDb()));
    }

    private sealed class ProofRecordingVerifier(bool result) : ILeanProofVerifier
    {
        public byte[]? VerifiedProof { get; private set; }

        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => result;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => result;

        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
        {
            VerifiedProof = proof.ToArray();
            return result;
        }

        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) =>
            throw new NotSupportedException();
    }

    /// <summary>Fails with <see cref="Error"/> unless the block carries a proof, recording the proof it was given.</summary>
    private sealed class RecordingValidator : IBlockValidator
    {
        public string Error { get; init; } = BlockErrorMessages.MissingDependencyProof;
        public byte[]? ValidatedProof { get; private set; }

        public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true)
        {
            ValidatedProof = block.StarkProof;
            error = block.StarkProof is null ? Error : null;
            return error is null;
        }

        public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) => ValidateSuggestedBlock(block, null!, out error);
        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) { error = null; return true; }
        public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) { error = null; return true; }
        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) { error = null; return true; }
        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) { error = null; return true; }
        public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) { error = null; return true; }
        public bool ValidateWithdrawals(Block block, out string? error) { error = null; return true; }
    }
}
