// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeLeanProofVerifierTests
{
    private static readonly ILeanProofVerifier Native = NativeLeanProofVerifier.Instance;
    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "lean-vectors", name + ".bin"));
    internal static FrameDependency Dependency(string scheme) => new(scheme == "sphincs" ? Eip8288Constants.LeanSphincsScheme : Eip8288Constants.LeanStarkScheme,
        new ValueHash256(Fixture(scheme + "-message")), new ValueHash256(Fixture(scheme + "-key")));
    internal static byte[] Witness(string scheme) => Fixture(scheme == "sphincs" ? "sphincs-signature" : "stark-proof");

    [OneTimeSetUp]
    public void EnsureNativeLibraryLoads()
    {
        NativeLeanProofVerifier.Instance.EnsureAvailable();
        Assert.That(NativeLeanProofVerifier.AbiVersion, Is.EqualTo(2u));
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
        byte[][] witnesses = new byte[count][];
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
    public void Recursive_proof_compresses_sphincs_and_carries_verified_starks()
    {
        FrameDependency sphincs = Dependency("sphincs");
        FrameDependency stark = Dependency("stark");
        AggregationInput leafInput = new() { Deps = [sphincs], Witnesses = [Witness("sphincs")] };
        ValueHash256 leafHash = Eip8288Dependencies.ComputeDepsHash([sphincs]);
        byte[] leaf = Native.ProveRecursiveStark(leafHash, Eip8288Constants.AggregatedVk, leafInput);
        Assert.That(Native.VerifyRecursiveStark(leafHash, Eip8288Constants.AggregatedVk, leaf), Is.True);
        AggregationInput mixedInput = new()
        {
            Deps = [stark],
            Witnesses = [Witness("stark")],
            RecursiveProofs = [new RecursiveProofInput([sphincs], leaf)]
        };
        ValueHash256 mixedHash = Eip8288Dependencies.ComputeDepsHash([stark, sphincs]);
        byte[] mixed = Native.ProveRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, mixedInput);
        Assert.That(Native.VerifyRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, mixed), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Native.VerifyRecursiveStark(leafHash, Eip8288Constants.AggregatedVk, mixed), Is.False);
            Assert.That(Native.VerifyRecursiveStark(mixedHash, new byte[32], mixed), Is.False);
            Assert.That(Native.VerifyRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, [.. mixed, 0]), Is.False);
        }
        mixed[^1] ^= 1;
        Assert.That(Native.VerifyRecursiveStark(mixedHash, Eip8288Constants.AggregatedVk, mixed), Is.False);
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
