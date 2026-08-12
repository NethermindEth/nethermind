// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;
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
    public void EnsureNativeLibraryLoads()
    {
        try
        {
            _ = NativeLeanProofVerifier.AbiVersion;
        }
        catch (DllNotFoundException e)
        {
            Assert.Ignore($"native nethermind_lean library not available: {e.Message}");
        }
    }

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

    private static (Block Block, BlockHeader Parent) BlockWithNativeProof(bool tamper)
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        Block block = Build.A.Block.WithParent(parent).TestObject;

        ValueHash256 depsHash = Eip8288Dependencies.ComputeBlockDepsHash(block);
        // Produced exactly as BlockProcessor does on ProducingBlock.
        byte[] proof = PlaceholderLeanProofVerifier.ProveRecursive(in depsHash, Eip8288Constants.AggregatedVk);
        if (tamper) proof[0] ^= 0xFF;
        block.Header.RecursiveStark = new RecursiveStark(proof, new Hash256(depsHash));
        return (block, parent);
    }

    private static BlockValidator CreateValidator()
    {
        IHeaderValidator headerValidator = Substitute.For<IHeaderValidator>();
        headerValidator.Validate(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()).Returns(true);
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8288Enabled.Returns(true);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        return new BlockValidator(
            Substitute.For<ITxValidator>(),
            headerValidator,
            Substitute.For<IUnclesValidator>(),
            specProvider,
            LimboLogs.Instance,
            NativeLeanProofVerifier.Instance);
    }
}
