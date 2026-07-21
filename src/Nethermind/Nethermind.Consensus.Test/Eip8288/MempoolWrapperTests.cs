// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Eip8288;

public class MempoolWrapperTests
{
    private static readonly ILeanProofVerifier Accepting = new FakeLeanProofVerifier(true);

    private static FrameDependency Sphincs(string tag) =>
        new(Eip8288Constants.LeanSphincsScheme, Keccak.Compute(tag), Keccak.Compute(tag + "-vk"));

    private static FrameDependency Stark(string tag) =>
        new(Eip8288Constants.LeanStarkScheme, Keccak.Compute(tag), Keccak.Compute(tag + "-vk"));

    [Test]
    public void Direct_wrapper_round_trips()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1")), new WrapperTransaction(Keccak.Compute("tx2"))],
            Mode = MempoolWrapper.ModeDirect,
            Deps = [Sphincs("a"), Stark("b")],
            Proofs = [[1, 2], [3, 4]],
        };

        MempoolWrapper decoded = RoundTrip(wrapper);

        Assert.That(decoded.Mode, Is.EqualTo(MempoolWrapper.ModeDirect));
        Assert.That(decoded.Transactions.Count, Is.EqualTo(2));
        Assert.That(decoded.Transactions.All(t => t.IsHashOnly), Is.True);
        Assert.That(decoded.Deps, Is.EqualTo(wrapper.Deps));
        Assert.That(decoded.Proofs!.Count, Is.EqualTo(2));
        Assert.That(decoded.Proofs![1], Is.EqualTo(new byte[] { 3, 4 }));
    }

    [Test]
    public void Recursive_wrapper_round_trips()
    {
        List<FrameDependency> deps = [Sphincs("a")];
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = deps,
            RecursiveStark = new RecursiveStark([7, 8, 9], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        MempoolWrapper decoded = RoundTrip(wrapper);

        Assert.That(decoded.Mode, Is.EqualTo(MempoolWrapper.ModeRecursive));
        Assert.That(decoded.RecursiveStark!.StarkProof, Is.EqualTo(new byte[] { 7, 8, 9 }));
        Assert.That(decoded.RecursiveStark!.BlockDepsHash, Is.EqualTo(new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))));
    }

    [Test]
    public void Direct_wrapper_valid()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeDirect,
            Deps = [Sphincs("a"), Stark("b")],
            Proofs = [[1], [2]],
        };

        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error), Is.True, error);
    }

    [Test]
    public void Recursive_wrapper_valid()
    {
        List<FrameDependency> deps = [Sphincs("a")];
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = deps,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error), Is.True, error);
    }

    [Test]
    public void Direct_wrapper_rejects_proof_count_mismatch()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeDirect,
            Deps = [Sphincs("a"), Stark("b")],
            Proofs = [[1]],
        };

        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.ProofCountMismatch));
    }

    [Test]
    public void Recursive_wrapper_rejects_deps_hash_mismatch()
    {
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = [Sphincs("a")],
            RecursiveStark = new RecursiveStark([1], Keccak.Compute("wrong")),
        };

        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.DepsHashMismatch));
    }

    [Test]
    public void Wrapper_rejects_too_many_sig_deps()
    {
        List<FrameDependency> deps = Enumerable.Range(0, Eip8288Constants.MaxLeanSigDepsPerWrapper + 1).Select(i => Sphincs(i.ToString())).ToList();
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = deps,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.TooManySigDeps));
    }

    [Test]
    public void Wrapper_rejects_too_many_stark_deps()
    {
        List<FrameDependency> deps = [Stark("a"), Stark("b")];
        MempoolWrapper wrapper = new()
        {
            Transactions = [new WrapperTransaction(Keccak.Compute("tx1"))],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = deps,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(MempoolWrapperValidator.Validate(wrapper, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.TooManyStarkDeps));
    }

    private static MempoolWrapper RoundTrip(MempoolWrapper wrapper)
    {
        Rlp rlp = MempoolWrapperDecoder.Instance.Encode(wrapper);
        RlpReader reader = new(rlp.Bytes);
        return MempoolWrapperDecoder.Instance.Decode(ref reader);
    }
}
