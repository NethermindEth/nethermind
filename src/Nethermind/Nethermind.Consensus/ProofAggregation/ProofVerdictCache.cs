// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// A bounded cache of EIP-8288 proof verdicts keyed by <c>(keccak256(proof), get_deps_hash(covered))</c>, so a known proof
/// arriving with a different or unresolvable transaction list, or in another inclusion list, is not verified again.
/// </summary>
/// <remarks>
/// <c>covered</c> is a mode-1 wrapper's or inclusion list's dependency set, or a mode-0 witness's single dependency. Both
/// verdicts are kept: verification is deterministic for a fixed <see cref="Eip8288Constants.AggregatedVk"/>. One cache exists per
/// backend verifier, so independent verifiers (and tests) never share verdicts.
/// </remarks>
public sealed class ProofVerdictCache
{
    /// <summary>Verdicts retained per verifier; the oldest is evicted first.</summary>
    public const int Capacity = 4096;

    private static readonly ConditionalWeakTable<ILeanProofVerifier, ProofVerdictCache> Caches = [];

    private readonly Lock _gate = new();
    private readonly Dictionary<(ValueHash256 Proof, ValueHash256 Dependencies), bool> _verdicts = [];
    private readonly Queue<(ValueHash256 Proof, ValueHash256 Dependencies)> _order = new();

    /// <summary>The cache shared by every caller of <paramref name="verifier"/>.</summary>
    public static ProofVerdictCache For(ILeanProofVerifier verifier) => Caches.GetValue(verifier, static _ => new());

    public int Count
    {
        get { lock (_gate) return _verdicts.Count; }
    }

    /// <summary>Verifies a recursive aggregate proof against <paramref name="depsHash"/> and <see cref="Eip8288Constants.AggregatedVk"/>.</summary>
    public static bool VerifyRecursive(ILeanProofVerifier verifier, in ValueHash256 depsHash, byte[] proof)
    {
        ValueHash256 deps = depsHash;
        return For(verifier).Verify(proof, in deps, () => verifier.VerifyRecursiveStark(in deps, Eip8288Constants.AggregatedVk, proof));
    }

    /// <summary>Verifies a mode-0 direct witness for its single dependency.</summary>
    public static bool VerifyDirect(ILeanProofVerifier verifier, FrameDependency dependency, byte[] witness)
    {
        Func<bool>? check = dependency.Scheme switch
        {
            Eip8288Constants.LeanSphincsScheme => () => verifier.VerifyLeanSphincs(dependency.DataHash, dependency.VerificationKey, witness),
            Eip8288Constants.LeanStarkScheme => () => verifier.VerifyLeanStark(dependency.DataHash, dependency.VerificationKey, witness),
            _ => null
        };
        if (check is null) return false;
        ValueHash256 covered = Eip8288Dependencies.ComputeDepsHash([dependency]);
        return For(verifier).Verify(witness, in covered, check);
    }

    private bool Verify(byte[] proof, in ValueHash256 dependencies, Func<bool> verify)
    {
        ValueHash256 proofHash = ValueKeccak.Compute(proof);
        (ValueHash256, ValueHash256) key = (proofHash, dependencies);
        lock (_gate)
            if (_verdicts.TryGetValue(key, out bool known)) return known;
        bool valid = verify();
        // Callers may mutate proof arrays during verification; never record a verdict under stale bytes.
        if (ValueKeccak.Compute(proof) != proofHash) return false;
        lock (_gate)
        {
            if (_verdicts.TryAdd(key, valid))
            {
                _order.Enqueue(key);
                if (_order.Count > Capacity) _verdicts.Remove(_order.Dequeue());
            }
        }
        return valid;
    }
}
