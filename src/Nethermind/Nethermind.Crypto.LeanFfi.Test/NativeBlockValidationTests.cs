// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

/// <summary>
/// Live end-to-end test of the native Lean verifier on the real block-validation path: a block
/// carrying a recursive STARK (proof produced exactly as block production does) is validated by
/// <see cref="BlockValidator"/> wired with the native FFI <see cref="NativeLeanProofVerifier"/>.
/// </summary>
public class NativeBlockValidationTests
{
    [OneTimeSetUp]
    public void EnsureNativeLibraryLoads() => Assert.That(NativeLeanProofVerifier.AbiVersion, Is.EqualTo(23u));

    [Test]
    public void BlockValidator_with_native_verifier_accepts_produced_recursive_stark()
    {
        (Block block, BlockHeader parent) = BlockWithNativeProof(tamper: false);

        bool result = CreateValidator().ValidateSuggestedBlock(block, parent, out string? error);

        Assert.That(result, Is.True, error);
    }

    [Test]
    public void BlockValidator_with_native_verifier_rejects_tampered_recursive_stark()
    {
        (Block block, BlockHeader parent) = BlockWithNativeProof(tamper: true);

        CreateValidator().ValidateSuggestedBlock(block, parent, out string? error);

        Assert.That(error, Does.Contain("RecursiveStark"));
    }

    // EIP-8288 bounds a list by the output capacity, not by one step: a block over more raw leanSTARK witnesses than one step
    // verifies, and over 256 dependencies, verifies once leaves within one step each are folded.
    [Test]
    public async Task BlockValidator_with_native_verifier_accepts_a_list_wider_than_one_step()
    {
        const int signatures = Eip8288Constants.MaxRawWitnessesPerStep;
        const int starks = Eip8288Constants.MaxInnerProofsPerStep + 1;
        (FrameDependency Dependency, byte[] Witness)[] witnesses = await GenerateWitnesses(starks, signatures);
        RecursiveProofInput[] leaves = [Leaf(witnesses[..(starks - 1)]), Leaf(witnesses[(starks - 1)..(starks + signatures / 2)]),
            Leaf(witnesses[(starks + signatures / 2)..])];
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize(leaves.SelectMany(static leaf => leaf.InnerDeps));
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        byte[] proof = NativeLeanProofVerifier.Instance.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk,
            new AggregationInput { RecursiveProofs = leaves });
        (Block block, BlockHeader parent) = BlockWithProof(dependencies, proof);

        bool result = CreateValidator().ValidateSuggestedBlock(block, parent, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True, error);
            Assert.That(dependencies, Has.Count.EqualTo(starks + signatures));
            Assert.That(Eip8288Dependencies.CountLeanStark(dependencies), Is.EqualTo(starks));
        }

        static RecursiveProofInput Leaf((FrameDependency Dependency, byte[] Witness)[] raw)
        {
            List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(raw.Select(static w => w.Dependency));
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps);
            return new(deps, NativeLeanProofVerifier.Instance.ProveRecursiveStark(in hash, Eip8288Constants.AggregatedVk, new AggregationInput
            {
                Deps = [.. raw.Select(static w => w.Dependency)],
                Witnesses = [.. raw.Select(static w => (ReadOnlyMemory<byte>)w.Witness)]
            }));
        }
    }

    [Test]
    public void BlockValidator_with_native_verifier_requires_an_empty_proof_without_dependencies([Values] bool nonEmpty)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        ValueHash256 depsHash = Eip8288Dependencies.ComputeBlockDepsHash(block, FrameTxSigHash.ComputeValue);
        // The native backend proves the empty set as the EIP-8288 empty stark_proof; any nonempty proof is rejected.
        byte[] proof = nonEmpty
            ? [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
            : NativeLeanProofVerifier.Instance.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, new AggregationInput());
        block.StarkProof = proof;
        block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(proof), new Hash256(depsHash));

        bool result = CreateValidator().ValidateSuggestedBlock(block, parent, out string? error);

        Assert.That(result, Is.EqualTo(!nonEmpty), error);
    }

    private static (Block Block, BlockHeader Parent) BlockWithNativeProof(bool tamper)
    {
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize(
            [NativeLeanProofVerifierTests.Dependency("sphincs"), NativeLeanProofVerifierTests.Dependency("stark")]);
        byte[] proof = NativeLeanProofVerifierTests.MixedProof();
        if (tamper) proof[^1] ^= 0xFF;
        return BlockWithProof(dependencies, proof);
    }

    private static (Block Block, BlockHeader Parent) BlockWithProof(IReadOnlyList<FrameDependency> dependencies, byte[] proof)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [.. dependencies.Chunk(Eip8288Constants.MaxDependenciesPerFrame).Select(static frame => new TxFrame(FrameMode.DepVerify,
                FrameFlags.None, null, frame.Aggregate(0UL, static (gas, dependency) => gas + dependency.VerificationGas), UInt256.Zero,
                Eip8288Dependencies.Serialize(frame)))],
            FrameSignatures = [],
        };
        transaction.Hash = transaction.CalculateHash();
        Block block = Build.A.Block.WithParent(parent).WithTransactions(transaction).TestObject;

        ValueHash256 depsHash = Eip8288Dependencies.ComputeBlockDepsHash(block, FrameTxSigHash.ComputeValue);
        block.StarkProof = proof;
        block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(proof), new Hash256(depsHash));
        return (block, parent);
    }

    /// <summary>Distinct real dependencies from the fixtures tool: <paramref name="starks"/> leanSTARK ones, then
    /// <paramref name="signatures"/> leanSPHINCS ones, each with its witness.</summary>
    private static async Task<(FrameDependency Dependency, byte[] Witness)[]> GenerateWitnesses(int starks, int signatures)
    {
        (FrameDependency, byte[])[] witnesses = new (FrameDependency, byte[])[starks + signatures];
        await Parallel.ForAsync(0, witnesses.Length, async (i, _) =>
        {
            bool stark = i < starks;
            ValueHash256 message = ValueKeccak.Compute(BitConverter.GetBytes(i));
            byte[] output = await NativeBlockProductionTests.GenerateWitness(message, stark);
            ValueHash256 key = new(output.AsSpan(0, 32));
            witnesses[i] = stark
                ? (new FrameDependency(Eip8288Constants.LeanStarkScheme, message, key), output[32..])
                : (new FrameDependency(Eip8288Constants.LeanSphincsScheme, message, key), output);
        });
        return witnesses;
    }

    private static RecursiveStarkBlockValidator CreateValidator()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8288Enabled.Returns(true);
        spec.Eip8288AggregatedVk.Returns(Eip8288Verifier.PinnedAggregatedVk);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        return new RecursiveStarkBlockValidator(
            new BlockValidator(Always.Valid, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance),
            NativeLeanProofVerifier.Instance,
            specProvider,
            LimboLogs.Instance);
    }
}
