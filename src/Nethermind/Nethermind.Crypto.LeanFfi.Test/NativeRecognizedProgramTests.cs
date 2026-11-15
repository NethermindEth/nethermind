// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

/// <summary>EIP-8437 mode 2 against the native backend: the recognized programs' keys and native checks match the package's
/// equivalence vectors, and a raw signature proves into the leaf the batch guest folds into ordinary coverage.</summary>
[NonParallelizable]
public class NativeRecognizedProgramTests
{
    private static readonly Eip8288Verifier Batched = new(Eip8288Verifier.BatchedAggregatedVk);
    private static readonly ILeanProofVerifier Native = NativeLeanProofVerifier.Instance;
    private static readonly NativeRecognizedProgramVerifier Recognized = new(NativeRecognizedProgramVerifier.Profiles.Keys);

    private sealed record Vector(uint Profile, string Label, RawSignature Signature, ValueHash256? Claim, string Leaf);

    private static readonly Lazy<(Dictionary<uint, ValueHash256> Keys, Vector[] Vectors)> Vectors = new(static () =>
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "lean-vectors", "raw-vectors.json")));
        static byte[] Hex(JsonElement e) => Convert.FromHexString(e.GetString()![2..]);
        Dictionary<uint, ValueHash256> keys = json.RootElement.GetProperty("profiles").EnumerateArray()
            .ToDictionary(static p => p.GetProperty("id").GetUInt32(), static p => new ValueHash256(Hex(p.GetProperty("key"))));
        Vector[] vectors = [.. json.RootElement.GetProperty("vectors").EnumerateArray().Select(static v => new Vector(
            v.GetProperty("profile").GetUInt32(), v.GetProperty("label").GetString()!,
            new RawSignature(Hex(v.GetProperty("publicKey")), new ValueHash256(Hex(v.GetProperty("message"))), Hex(v.GetProperty("signature"))),
            v.GetProperty("claim").ValueKind == JsonValueKind.Null ? (ValueHash256?)null : new ValueHash256(Hex(v.GetProperty("claim"))),
            v.GetProperty("leaf").GetString()!))];
        return (keys, vectors);
    });

    [Test]
    public void Enabled_profiles_have_the_packaged_keys()
    {
        using (Assert.EnterMultipleScope())
        {
            foreach ((string name, uint id) in NativeRecognizedProgramVerifier.Profiles)
            {
                Assert.That(Recognized.Keys[Vectors.Value.Keys[id]], Is.EqualTo(id), name);
                Assert.That(new NativeRecognizedProgramVerifier([name]).Recognizes(Vectors.Value.Keys[id]), Is.True, name);
                Assert.That(new NativeRecognizedProgramVerifier([]).Recognizes(Vectors.Value.Keys[id]), Is.False, name);
            }
            Assert.That(() => new NativeRecognizedProgramVerifier(["falcon512"]), Throws.ArgumentException);
        }
    }

    // EIP-8437: conformance vectors establish that the native check accepts only what the leaf program proves, with its claim.
    [Test]
    public void Native_checks_reach_every_vector_verdict_and_claim()
    {
        Vector[] vectors = Vectors.Value.Vectors;
        Assert.That(vectors.Count(static v => v.Claim is null), Is.GreaterThan(30));
        using (Assert.EnterMultipleScope())
        {
            foreach (Vector vector in vectors)
            {
                bool valid = Recognized.Verify(Vectors.Value.Keys[vector.Profile], vector.Signature, out ValueHash256 claim);
                Assert.That(valid, Is.EqualTo(vector.Claim is not null), $"{vector.Profile}: {vector.Label}");
                if (vector.Claim is { } expected) Assert.That(claim, Is.EqualTo(expected), vector.Label);
                if (valid) Assert.That(vector.Leaf, Is.EqualTo("proves"), vector.Label);
                // A key of another program never recognizes the signature.
                uint other = vector.Profile % 3 + 1;
                Assert.That(Recognized.Verify(Vectors.Value.Keys[other], vector.Signature, out _), Is.False, $"{vector.Label} under profile {other}");
            }
        }
    }

    // The flow EIP-8437 mode 2 enables: a raw signature validates natively in a mode-2 wrapper; a prover's one-signature leaf is both a
    // direct witness (mode 0) and, folded by the batch guest, a mode-1 proof of the same dependency.
    [TestCase(3u)]
    [TestCase(2u)]
    public void Raw_signature_proves_into_ordinary_coverage(uint profile)
    {
        Vector honest = Vectors.Value.Vectors.First(v => v.Profile == profile && v.Claim is not null);
        ValueHash256 key = Vectors.Value.Keys[profile];
        FrameDependency dependency = new(Eip8288Constants.LeanStarkScheme, honest.Claim!.Value, key);
        Transaction transaction = NativeBatchLeafTests.DependentTransaction(0, dependency);
        MempoolWrapper raw = new()
        {
            Transactions = [transaction],
            Mode = MempoolWrapper.ModeRaw,
            Deps = [dependency],
            Proofs = [[]],
            RawWitnesses = [honest.Signature],
        };
        Assert.That(MempoolWrapperValidator.Validate(raw, Native, Batched, out string rawError, Recognized), Is.True, rawError);

        byte[] leaf = Recognized.ProveLeaf(key, [honest.Signature]);
        ValueHash256 depsHash = Batched.DepsHash([dependency]);
        byte[] aggregate = Native.ProveRecursiveStark(depsHash, Eip8288Constants.BatchedAggregatedVk,
            new() { BatchLeaves = [new BatchLeafInput(key, [honest.Claim!.Value], [dependency], leaf)] });

        MempoolWrapper direct = new() { Transactions = [transaction], Mode = MempoolWrapper.ModeDirect, Deps = [dependency], Proofs = [leaf] };
        MempoolWrapper recursive = new()
        {
            Transactions = [transaction],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = [dependency],
            RecursiveStark = new(aggregate, new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency]))),
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MempoolWrapperValidator.Validate(direct, Native, Batched, out string directError), Is.True, directError);
            Assert.That(MempoolWrapperValidator.Validate(recursive, Native, Batched, out string recursiveError), Is.True, recursiveError);
            Assert.That(MempoolWrapperValidator.Validate(raw, Native, Eip8288Verifier.Pinned, out _, Recognized), Is.False,
                "EIP-8288's guest takes no batch leaf");
        }
    }

    [Test]
    public void Prover_refuses_a_rejected_signature()
    {
        Vector rejected = Vectors.Value.Vectors.First(static v => v.Profile == 3 && v.Claim is null);
        Assert.That(() => Recognized.ProveLeaf(Vectors.Value.Keys[3], [rejected.Signature]), Throws.InvalidOperationException);
    }
}
