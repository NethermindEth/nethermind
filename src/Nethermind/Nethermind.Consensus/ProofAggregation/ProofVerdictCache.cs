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
/// arriving with a different or unresolvable transaction list, or committed to by an inclusion list, is not verified again.
/// </summary>
/// <remarks>
/// <c>covered</c> is a mode-1 wrapper's or <c>il_proof</c>'s dependency set, or a mode-0 witness's single dependency. Keys also
/// carry the verification kind and the verifier configuration, so a direct witness never satisfies an aggregate lookup or the
/// reverse, and a verdict under one <see cref="Eip8288Constants.AggregatedVk"/> never answers another. Both verdicts are kept:
/// verification is deterministic for a fixed configuration. A recursive verdict
/// keeps its preimage <c>proof_deps</c>, so an inclusion-list commitment to a held proof is checked by lookup and subset test
/// (EIPs#12475). One cache exists per backend verifier, so independent verifiers (and tests) never share verdicts.
/// </remarks>
public sealed class ProofVerdictCache
{
    /// <summary>Verdicts retained per verifier; the oldest is evicted first.</summary>
    public const int Capacity = 4096;

    /// <summary>Dependencies retained across all preimages, bounding their memory independently of the verdict count.</summary>
    public const int MaxPreimageDependencies = 64 * 1024;

    private static readonly ConditionalWeakTable<ILeanProofVerifier, ProofVerdictCache> Caches = [];

    private readonly Lock _gate = new();
    private readonly Dictionary<VerdictKey, bool> _verdicts = [];
    private readonly Queue<VerdictKey> _order = new();

    /// <summary>What was verified: a recursive aggregate proof, or a direct witness for one dependency's scheme.</summary>
    private enum VerificationKind : byte { Aggregate, Direct }

    private readonly record struct VerdictKey(VerificationKind Kind, ValueHash256 Configuration, ValueHash256 Proof, ValueHash256 Dependencies);

    /// <summary>The verifier configuration verdicts are scoped to: the pinned <see cref="Eip8288Constants.AggregatedVk"/>, which
    /// fixes the aggregate circuit and the enabled schemes' verification parameters.</summary>
    private static readonly ValueHash256 Configuration = ValueKeccak.Compute(Eip8288Constants.AggregatedVk);
    private readonly Dictionary<ValueHash256, FrameDependency[]> _preimages = [];
    private readonly Queue<ValueHash256> _preimageOrder = new();
    private int _preimageDependencies;

    /// <summary>The cache shared by every caller of <paramref name="verifier"/>.</summary>
    public static ProofVerdictCache For(ILeanProofVerifier verifier) => Caches.GetValue(verifier, static _ => new());

    public int Count
    {
        get { lock (_gate) return _verdicts.Count; }
    }

    /// <summary>Verifies a recursive aggregate proof against <paramref name="depsHash"/> and <see cref="Eip8288Constants.AggregatedVk"/>.</summary>
    /// <param name="proofDeps">The canonical dependency list hashing to <paramref name="depsHash"/>, kept with the verdict.</param>
    public static bool VerifyRecursive(ILeanProofVerifier verifier, in ValueHash256 depsHash, byte[] proof, IReadOnlyList<FrameDependency>? proofDeps = null)
    {
        ValueHash256 deps = depsHash;
        ProofVerdictCache cache = For(verifier);
        bool valid = cache.Verify(VerificationKind.Aggregate, proof, in deps, () => verifier.VerifyRecursiveStark(in deps, Eip8288Constants.AggregatedVk, proof));
        if (valid && proofDeps is not null) cache.RememberPreimage(in deps, proofDeps);
        return valid;
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
        return For(verifier).Verify(VerificationKind.Direct, witness, in covered, check);
    }

    /// <summary>The known verdict for an aggregate proof under <paramref name="depsHash"/>, if any.</summary>
    public bool? TryGetVerdict(in ValueHash256 proofHash, in ValueHash256 depsHash)
    {
        lock (_gate)
            return _verdicts.TryGetValue(new(VerificationKind.Aggregate, Configuration, proofHash, depsHash), out bool valid) ? valid : null;
    }

    /// <summary>The known preimage <c>proof_deps</c> of <paramref name="depsHash"/>, from any verified proof.</summary>
    public bool TryGetPreimage(in ValueHash256 depsHash, out FrameDependency[] proofDeps)
    {
        lock (_gate) return _preimages.TryGetValue(depsHash, out proofDeps!);
    }

    private void RememberPreimage(in ValueHash256 depsHash, IReadOnlyList<FrameDependency> proofDeps)
    {
        lock (_gate)
            if (_preimages.ContainsKey(depsHash) || proofDeps.Count > MaxPreimageDependencies) return;
        // Callers pass the list the proof verified against; a mismatched list would let a subset check pass wrongly.
        FrameDependency[] canonical = [.. Eip8288Dependencies.Canonicalize(proofDeps)];
        if (canonical.Length != proofDeps.Count || Eip8288Dependencies.ComputeDepsHash(canonical) != depsHash) return;
        lock (_gate)
        {
            if (!_preimages.TryAdd(depsHash, canonical)) return;
            _preimageOrder.Enqueue(depsHash);
            _preimageDependencies += canonical.Length;
            while (_preimageDependencies > MaxPreimageDependencies && _preimageOrder.TryDequeue(out ValueHash256 oldest))
                if (_preimages.Remove(oldest, out FrameDependency[]? evicted)) _preimageDependencies -= evicted.Length;
        }
    }

    private bool Verify(VerificationKind kind, byte[] proof, in ValueHash256 dependencies, Func<bool> verify)
    {
        ValueHash256 proofHash = ValueKeccak.Compute(proof);
        VerdictKey key = new(kind, Configuration, proofHash, dependencies);
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
