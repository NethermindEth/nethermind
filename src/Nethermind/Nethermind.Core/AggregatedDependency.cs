// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>An EIP-8141 claim dependency as EIP-8288 aggregates it: its resolved message (<c>data_hash</c>) and public key
/// <c>PK.seed || PK.root</c> (<c>verification_key_hash</c>).</summary>
/// <remarks>Built only by <see cref="Eip8288Dependencies.Map"/>, which is the single claim-to-dependency mapping.</remarks>
public readonly record struct AggregatedDependency(ValueHash256 Message, ValueHash256 PublicKey)
{
    /// <summary>Writes the 96-byte triple the recursive guest takes: <c>bytes32_be(LEANSPHINCS_SCHEME) || data_hash || verification_key</c>.</summary>
    public void WriteTo(Span<byte> destination)
    {
        destination[..31].Clear();
        destination[31] = Eip8288Constants.LeanSphincsScheme;
        Message.Bytes.CopyTo(destination[32..64]);
        PublicKey.Bytes.CopyTo(destination[64..96]);
    }
}

/// <summary>EIP-8288 dependency-list operations shared by aggregation and block validation.</summary>
public static class Eip8288Dependencies
{
    /// <summary>Whether EIP-8288 aggregates the claims of <paramref name="verifier"/>, whose witnesses an aggregated
    /// dependency proof then replaces.</summary>
    /// <remarks>Only <c>LEANSPHINCS_VERIFIER</c>, the one protocol verifier whose scheme the recursive guest verifies. Other
    /// protocol verifiers and custom contract verifiers keep their raw witnesses: a contract verifier's semantics are its
    /// code, which the guest does not run.</remarks>
    public static bool IsAggregatable(Address verifier) => verifier == Eip8141Constants.LeanSphincsVerifierAddress;

    /// <summary>Maps an aggregatable claim's EIP-8141 resolved dependency to its EIP-8288 dependency.</summary>
    /// <remarks><c>(LEANSPHINCS_VERIFIER, resolved_key, resolved_msg, gas, witness_len)</c> maps to
    /// <c>(LEANSPHINCS_SCHEME, data_hash = resolved_msg, verification_key_hash = resolved_key)</c>; the gas and witness
    /// length are fixed by the verifier, so the mapping is one to one.</remarks>
    public static AggregatedDependency Map(in ValueHash256 resolvedKey, in ValueHash256 resolvedMsg) => new(resolvedMsg, resolvedKey);

    /// <summary>Spec <c>get_deps_hash</c>: <c>DEPS_HASH</c> over the canonical dependencies' 96-byte triples.</summary>
    /// <remarks>EIP-8288 leaves <c>DEPS_HASH</c> TBD; this uses Keccak-256 over the triples, as the pinned recursive guest
    /// commits its dependency digest.</remarks>
    public static ValueHash256 ComputeDepsHash(IReadOnlyList<AggregatedDependency> dependencies)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> triple = stackalloc byte[Eip8288Constants.DependencyTripleLength];
        foreach (AggregatedDependency dependency in Canonicalize(dependencies))
        {
            dependency.WriteTo(triple);
            hash.Update(triple);
        }

        return hash.GenerateValueHash();
    }

    /// <summary>Sorts by message then key, as the guest orders triples of one scheme, and deduplicates.</summary>
    public static List<AggregatedDependency> Canonicalize(IEnumerable<AggregatedDependency> dependencies)
    {
        List<AggregatedDependency> result = [.. dependencies];
        result.Sort(static (left, right) =>
        {
            int comparison = left.Message.CompareTo(right.Message);
            return comparison != 0 ? comparison : left.PublicKey.CompareTo(right.PublicKey);
        });
        int unique = 0;
        for (int i = 0; i < result.Count; i++)
        {
            if (unique == 0 || result[i] != result[unique - 1]) result[unique++] = result[i];
        }

        result.RemoveRange(unique, result.Count - unique);
        return result;
    }

    /// <summary>EIP-8288 discards: the dependencies the input proofs cover that the statement does not retain, in canonical order.</summary>
    /// <exception cref="ArgumentException">A retained dependency is not covered by the input proofs.</exception>
    public static List<AggregatedDependency> DiscardDependencies(IEnumerable<AggregatedDependency> inputDeps, IEnumerable<AggregatedDependency> retainedDeps)
    {
        HashSet<AggregatedDependency> inputs = [.. inputDeps];
        HashSet<AggregatedDependency> retained = [.. retainedDeps];
        if (!retained.IsSubsetOf(inputs))
            throw new ArgumentException("Retained dependencies must be covered by the input proofs.", nameof(retainedDeps));
        inputs.ExceptWith(retained);
        return Canonicalize(inputs);
    }
}
