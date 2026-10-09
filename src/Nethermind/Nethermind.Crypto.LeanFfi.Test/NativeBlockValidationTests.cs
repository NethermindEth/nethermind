// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
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
    public void EnsureNativeLibraryLoads() => Assert.That(NativeLeanProofVerifier.AbiVersion, Is.EqualTo(6u));

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

    [Test]
    public void BlockValidator_with_native_verifier_requires_an_empty_proof_without_dependencies([Values] bool oldEnvelope)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;
        ValueHash256 depsHash = Eip8288Dependencies.ComputeBlockDepsHash(block);
        // The native backend proves the empty set as the EIP-8288 empty stark_proof; the former 12-byte envelope is rejected.
        byte[] proof = oldEnvelope
            ? [(byte)'N', (byte)'L', (byte)'R', (byte)'3', 0, 0, 0, 0, 0, 0, 0, 0]
            : NativeLeanProofVerifier.Instance.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, new AggregationInput());
        block.Header.RecursiveStark = new RecursiveStark(proof, new Hash256(depsHash));

        bool result = CreateValidator().ValidateSuggestedBlock(block, parent, out string? error);

        Assert.That(result, Is.EqualTo(!oldEnvelope), error);
    }

    private static (Block Block, BlockHeader Parent) BlockWithNativeProof(bool tamper)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize(
            [NativeLeanProofVerifierTests.Dependency("sphincs"), NativeLeanProofVerifierTests.Dependency("stark")]);
        ulong gas = 0;
        foreach (FrameDependency dependency in dependencies) gas += dependency.VerificationGas;
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(FrameMode.DepVerify, FrameFlags.None, null, gas, UInt256.Zero, Eip8288Dependencies.Serialize(dependencies))],
            FrameSignatures = [],
        };
        transaction.Hash = transaction.CalculateHash();
        Block block = Build.A.Block.WithParent(parent).WithTransactions(transaction).TestObject;

        ValueHash256 depsHash = Eip8288Dependencies.ComputeBlockDepsHash(block);
        byte[] proof = NativeLeanProofVerifierTests.MixedProof();
        if (tamper) proof[^1] ^= 0xFF;
        block.Header.RecursiveStark = new RecursiveStark(proof, new Hash256(depsHash));
        return (block, parent);
    }

    private static BlockValidator CreateValidator()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8288Enabled.Returns(true);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        return new BlockValidator(
            Always.Valid,
            Always.Valid,
            Always.Valid,
            specProvider,
            LimboLogs.Instance,
            NativeLeanProofVerifier.Instance);
    }
}
