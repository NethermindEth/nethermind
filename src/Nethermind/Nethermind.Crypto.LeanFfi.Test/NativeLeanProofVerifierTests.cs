// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeLeanProofVerifierTests
{
    private static readonly ILeanProofVerifier Native = NativeLeanProofVerifier.Instance;
    private static readonly Lazy<byte[]> VerifiedMixedProof = new(CreateMixedProof);
    internal static byte[] MixedProof() => (byte[])VerifiedMixedProof.Value.Clone();

    private static byte[] CreateMixedProof()
    {
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize([Dependency("sphincs"), Dependency("stark")]);
        AggregationInput input = new()
        {
            Deps = dependencies,
            Witnesses = [Witness("sphincs"), Witness("stark")]
        };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        byte[] proof = RecursiveStarkAggregator.Prove(input, Native, in hash);
        Assert.That(Native.VerifyRecursiveStark(hash, Eip8288Constants.AggregatedVk, proof), Is.True);
        AssertMixedEnvelope(proof, dependencies);
        return proof;
    }

    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "lean-vectors", name + ".bin"));
    internal static FrameDependency Dependency(string scheme) => new(scheme == "sphincs" ? Eip8288Constants.LeanSphincsScheme : Eip8288Constants.LeanStarkScheme,
        new ValueHash256(Fixture(scheme + "-message")), new ValueHash256(Fixture(scheme + "-key")));
    internal static byte[] Witness(string scheme) => Fixture(scheme == "sphincs" ? "sphincs-signature" : "stark-proof");

    [OneTimeSetUp]
    public void EnsureNativeLibraryLoads()
    {
        NativeLeanProofVerifier.Instance.EnsureAvailable();
        Assert.That(NativeLeanProofVerifier.AbiVersion, Is.EqualTo(5u));
        Assert.That(NativeLeanProofVerifier.AggregatedVerificationKey, Is.EqualTo(Eip8288Constants.AggregatedVk.ToArray()));
    }

    [TestCase("sphincs")]
    [TestCase("stark")]
    public void Real_witness_binds_message_key_and_proof(string scheme)
    {
        FrameDependency dep = Dependency(scheme);
        byte[] witness = Witness(scheme);
        bool Verify(ValueHash256 message, ValueHash256 key, byte[] proof) => scheme == "sphincs"
            ? Native.VerifyLeanSphincs(message, key, proof)
            : Native.VerifyLeanStark(message, key, proof);
        Assert.That(Verify(dep.DataHash, dep.VerificationKey, witness), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Verify(default, dep.VerificationKey, witness), Is.False);
            Assert.That(Verify(dep.DataHash, default, witness), Is.False);
            if (scheme == "sphincs") Assert.That(Verify(dep.DataHash, new ValueHash256(witness.AsSpan(0, 32)), witness), Is.False);
            Assert.That(Verify(dep.DataHash, dep.VerificationKey, []), Is.False);
            Assert.That(Verify(dep.DataHash, dep.VerificationKey, witness[..^1]), Is.False);
        }
        witness[^1] ^= 1;
        Assert.That(Verify(dep.DataHash, dep.VerificationKey, witness), Is.False);
    }

    [TestCase(2, false)]
    [TestCase(4, false)]
    [TestCase(16, false)]
    [TestCase(2, true)]
    [TestCase(16, true)]
    public void Aggregation_binds_distinct_signature_claims(int count, bool distinctKeys)
    {
        byte[] fixtures = Fixture(distinctKeys ? "sphincs-multi-keys" : "sphincs-multi");
        FrameDependency[] deps = new FrameDependency[count];
        ReadOnlyMemory<byte>[] witnesses = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * (32 + Eip8288Constants.LeanSphincsWitnessBytes);
            ValueHash256 key = ValueKeccak.Compute(fixtures.AsSpan(offset + 32, 32));
            deps[i] = new(Eip8288Constants.LeanSphincsScheme, new ValueHash256(fixtures.AsSpan(offset, 32)), key);
            witnesses[i] = fixtures.AsSpan(offset + 32, Eip8288Constants.LeanSphincsWitnessBytes).ToArray();
        }
        ValueHash256 commitment = Eip8288Dependencies.ComputeDepsHash(deps);
        byte[] proof = Native.ProveRecursiveStark(commitment, Eip8288Constants.AggregatedVk, new() { Deps = deps, Witnesses = witnesses });
        Assert.That(Native.VerifyRecursiveStark(commitment, Eip8288Constants.AggregatedVk, proof), Is.True);
        deps[^1] = deps[0];
        Assert.That(Native.VerifyRecursiveStark(Eip8288Dependencies.ComputeDepsHash(deps), Eip8288Constants.AggregatedVk, proof), Is.False);
    }

    [Test]
    public void Default_child_input_is_rejected_as_an_argument_error()
    {
        AggregationInput input = new() { RecursiveProofs = [default] };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([]);
        Assert.Throws<ArgumentException>(() => Native.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, input));
    }

    [Test]
    public void Two_maximum_child_proofs_fit_the_aggregation_input_budget()
    {
        RecursiveProofInput child = new([Dependency("sphincs")], new byte[Eip8288Constants.MaxProofBytes]);
        byte[] encoded = NativeLeanProofVerifier.SerializeInput(new() { RecursiveProofs = [child, child] });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded.Length, Is.GreaterThan(2 * Eip8288Constants.MaxProofBytes));
            Assert.That(encoded.Length, Is.LessThanOrEqualTo(Eip8288Constants.MaxAggregationInputBytes));
        }
        Assert.Throws<ArgumentException>(() => NativeLeanProofVerifier.SerializeInput(new() { RecursiveProofs = [child, child, child] }));
    }

    [Test]
    public void Mixed_recursive_proof_verifies_generic_leaf_then_union_and_discard()
    {
        FrameDependency sphincs = Dependency("sphincs");
        FrameDependency stark = Dependency("stark");
        ValueHash256 genericHash = Eip8288Dependencies.ComputeDepsHash([stark]);
        byte[] genericLeaf = Native.ProveRecursiveStark(genericHash, Eip8288Constants.AggregatedVk,
            new() { Deps = [stark], Witnesses = [Witness("stark")] });
        Assert.That(Native.VerifyRecursiveStark(genericHash, Eip8288Constants.AggregatedVk, genericLeaf), Is.True);
        AssertMixedEnvelope(genericLeaf, [stark]);
        AggregationInput mixedInput = new()
        {
            Deps = [sphincs],
            Witnesses = [Witness("sphincs")],
            RecursiveProofs = [new RecursiveProofInput([stark], genericLeaf)]
        };
        ValueHash256 mixedHash = Eip8288Dependencies.ComputeDepsHash([stark, sphincs]);
        byte[] mixed = Native.ProveRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, mixedInput);
        Assert.That(Native.VerifyRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, mixed), Is.True);
        AssertMixedEnvelope(mixed, [stark, sphincs]);
        byte[] selected = Native.ProveRecursiveStark(genericHash, Eip8288Constants.AggregatedVk, new()
        {
            RecursiveProofs = [new([stark, sphincs], mixed)],
            Discards = [sphincs]
        });
        Assert.That(Native.VerifyRecursiveStark(genericHash, Eip8288Constants.AggregatedVk, selected), Is.True);
        AssertMixedEnvelope(selected, [stark]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Native.VerifyRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, selected), Is.False);
            Assert.That(Native.VerifyRecursiveStark(genericHash, Eip8288Constants.AggregatedVk, mixed), Is.False);
            Assert.That(Native.VerifyRecursiveStark(mixedHash, new byte[32], mixed), Is.False);
            Assert.That(Native.VerifyRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, [.. mixed, 0]), Is.False);
            Assert.That(Native.VerifyRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, mixed[..^1]), Is.False);
        }
        AggregationInput missing = new() { RecursiveProofs = [new([stark, sphincs], genericLeaf)] };
        Assert.Throws<InvalidOperationException>(() => Native.ProveRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, missing));
        mixed[^1] ^= 1;
        Assert.That(Native.VerifyRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, mixed), Is.False);
    }

    [Test]
    public void Empty_proof_uses_current_profile_and_rejects_the_carried_witness_profile()
    {
        ValueHash256 emptyHash = Eip8288Dependencies.ComputeDepsHash([]);
        byte[] empty = Native.ProveRecursiveStark(emptyHash, Eip8288Constants.AggregatedVk, new());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(empty, Is.EqualTo(new byte[] { (byte)'N', (byte)'L', (byte)'R', (byte)'3', 0, 0, 0, 0, 0, 0, 0, 0 }));
            Assert.That(Native.VerifyRecursiveStark(emptyHash, Eip8288Constants.AggregatedVk, empty), Is.True);
            Assert.That(Native.VerifyRecursiveStark(emptyHash, Eip8288Constants.AggregatedVk,
                [(byte)'N', (byte)'L', (byte)'R', (byte)'2', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), Is.False);
            Assert.That(Native.VerifyRecursiveStark(emptyHash,
                Convert.FromHexString("23305f2492843c52dfc0cf62ce46827b776071fcc6486504781ab8c8cf8ed387"), empty), Is.False);
        }
    }

    [TestCase("sphincs")]
    [TestCase("stark")]
    public void Invalid_raw_witness_cannot_be_hidden_by_a_valid_recursive_parent(string scheme)
    {
        FrameDependency dependency = Dependency(scheme);
        byte[] witness = Witness(scheme);
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        byte[] parent = Native.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk,
            new() { Deps = [dependency], Witnesses = [witness] });
        witness[^1] ^= 1;
        AggregationInput duplicate = new()
        {
            Deps = [dependency],
            Witnesses = [witness],
            RecursiveProofs = [new([dependency], parent)]
        };
        Assert.Throws<InvalidOperationException>(() => Native.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, duplicate));
    }

    internal static void AssertMixedEnvelope(byte[] proof, IReadOnlyList<FrameDependency> dependencies)
    {
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(dependencies);
        Assert.That(proof.Length, Is.GreaterThanOrEqualTo(12 + canonical.Count * Eip8288Constants.DependencyTripleLength));
        int payloadOffset = 8 + canonical.Count * Eip8288Constants.DependencyTripleLength;
        int payloadBytes = BinaryPrimitives.ReadInt32LittleEndian(proof.AsSpan(payloadOffset, 4));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof.AsSpan(0, 4).ToArray(), Is.EqualTo("NLR3"u8.ToArray()));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(proof.AsSpan(4, 4)), Is.EqualTo(canonical.Count));
            Assert.That(proof.AsSpan(8, canonical.Count * Eip8288Constants.DependencyTripleLength).ToArray(),
                Is.EqualTo(Eip8288Dependencies.Serialize(canonical)));
            Assert.That(payloadBytes, Is.InRange(canonical.Count == 0 ? 0 : 1, Eip8288Constants.MaxMixedGuestProofBytes));
            Assert.That(proof.Length, Is.EqualTo(payloadOffset + 4 + payloadBytes), "one guest proof has no carried-witness trailer");
        }
    }

    [Test]
    public void Discarding_claims_cannot_add_unproved_dependencies()
    {
        FrameDependency sphincs = Dependency("sphincs");
        FrameDependency stark = Dependency("stark");
        AggregationInput input = new() { Deps = [sphincs, stark, sphincs], Witnesses = [Witness("sphincs"), Witness("stark"), Witness("sphincs")], Discards = [sphincs] };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([stark]);
        byte[] proof = Native.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, input);
        Assert.That(Native.VerifyRecursiveStark(hash, Eip8288Constants.AggregatedVk, proof), Is.True);
        ValueHash256 invented = Eip8288Dependencies.ComputeDepsHash([sphincs, stark]);
        Assert.Throws<InvalidOperationException>(() => Native.ProveRecursiveStark(invented, Eip8288Constants.AggregatedVk, input));
    }
}
