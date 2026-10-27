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
/// The two bounded EIP-8288 proof-verdict caches, so a known proof arriving with a different or unresolvable transaction list,
/// or committed to by an inclusion list, is not verified again under the same verifier.
/// </summary>
/// <remarks>
/// Direct-witness verdicts are keyed by <c>(dep, keccak256(witness), scheme_params)</c> and aggregate-proof verdicts by
/// <c>(keccak256(stark_proof), get_deps_hash(deps), AGGREGATED_VK)</c>. A lookup consults only the cache of the operation it
/// would run, so a direct witness placed in a one-dependency mode-1 wrapper misses, and a verdict applies only under the
/// verifier in its key, so a fork that changes the verifier misses every old verdict. An aggregate verdict keeps its preimage
/// <c>proof_deps</c>, so an inclusion-list commitment to a held proof is checked by lookup and subset test. One cache exists per
/// backend verifier, so independent verifiers (and tests) never share verdicts.
/// </remarks>
public sealed class ProofVerdictCache
{
    /// <summary>Verdicts retained per cache; the oldest is evicted first.</summary>
    public const int Capacity = 4096;

    /// <summary>Dependencies retained across all preimages, bounding their memory independently of the verdict count.</summary>
    public const int MaxPreimageDependencies = 64 * 1024;

    private static readonly ConditionalWeakTable<ILeanProofVerifier, ProofVerdictCache> Caches = [];

    private readonly Lock _gate = new();
    private readonly BoundedVerdicts<AggregateKey> _aggregate = new();
    private readonly BoundedVerdicts<DirectKey> _direct = new();
    private readonly Dictionary<ValueHash256, FrameDependency[]> _preimages = [];
    private readonly Queue<ValueHash256> _preimageOrder = new();
    private int _preimageDependencies;

    private readonly record struct AggregateKey(ValueHash256 ProofHash, ValueHash256 DepsHash, ValueHash256 AggregatedVk);

    private readonly record struct DirectKey(FrameDependency Dependency, ValueHash256 WitnessHash, ValueHash256 SchemeParams);

    /// <summary>The cache shared by every caller of <paramref name="verifier"/>.</summary>
    public static ProofVerdictCache For(ILeanProofVerifier verifier) => Caches.GetValue(verifier, static _ => new());

    /// <summary>Aggregate-proof verdicts held.</summary>
    public int Count
    {
        get { lock (_gate) return _aggregate.Count; }
    }

    /// <summary>Direct-witness verdicts held.</summary>
    public int DirectCount
    {
        get { lock (_gate) return _direct.Count; }
    }

    /// <summary>Verifies a recursive aggregate proof against <paramref name="depsHash"/> under <paramref name="forkVerifier"/>'s
    /// <c>AGGREGATED_VK</c>.</summary>
    /// <param name="proofDeps">The canonical dependency list hashing to <paramref name="depsHash"/>, kept with the verdict.</param>
    public static bool VerifyRecursive(ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, in ValueHash256 depsHash, byte[] proof,
        IReadOnlyList<FrameDependency>? proofDeps = null)
    {
        ValueHash256 deps = depsHash;
        ValueHash256 key = forkVerifier.AggregatedVk;
        ProofVerdictCache cache = For(verifier);
        ValueHash256 proofHash = ValueKeccak.Compute(proof);
        bool valid = cache.Verify(cache._aggregate, new AggregateKey(proofHash, deps, key), proof, proofHash,
            () => verifier.VerifyRecursiveStark(in deps, key.Bytes, proof));
        if (valid && proofDeps is not null) cache.RememberPreimage(in deps, proofDeps);
        return valid;
    }

    /// <summary>Records an <c>INVALID</c> aggregate verdict for a proof rejected by its shape, without verification.</summary>
    /// <remarks>EIP-8288 inclusion-list proofs: the key fixes the proof bytes and exact dependency list, so the rejection is final
    /// under <paramref name="forkVerifier"/>. A verdict already held under the key is kept.</remarks>
    public static void RecordInvalidRecursive(ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, in ValueHash256 proofHash,
        in ValueHash256 depsHash)
    {
        ProofVerdictCache cache = For(verifier);
        lock (cache._gate) cache._aggregate.Add(new AggregateKey(proofHash, depsHash, forkVerifier.AggregatedVk), false);
    }

    /// <summary>Verifies a mode-0 direct witness for its single dependency under <paramref name="forkVerifier"/>'s parameters for the
    /// dependency's scheme. Only leanSPHINCS is enabled; any other scheme is rejected.</summary>
    public static bool VerifyDirect(ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, FrameDependency dependency, byte[] witness)
    {
        if (dependency.Scheme != Eip8288Constants.LeanSphincsScheme) return false;
        Func<bool> check = () => verifier.VerifyLeanSphincs(dependency.DataHash, dependency.VerificationKey, witness);
        ProofVerdictCache cache = For(verifier);
        ValueHash256 witnessHash = ValueKeccak.Compute(witness);
        return cache.Verify(cache._direct, new DirectKey(dependency, witnessHash, forkVerifier.SchemeParams(dependency.Scheme)), witness, witnessHash, check);
    }

    /// <summary>The known aggregate verdict for a proof under <paramref name="depsHash"/> and <paramref name="aggregatedVk"/>, if any.</summary>
    public bool? TryGetVerdict(in ValueHash256 proofHash, in ValueHash256 depsHash, in ValueHash256 aggregatedVk)
    {
        lock (_gate) return _aggregate.TryGet(new AggregateKey(proofHash, depsHash, aggregatedVk), out bool valid) ? valid : null;
    }

    /// <summary>The known direct-witness verdict for <paramref name="witnessHash"/> under <paramref name="forkVerifier"/>, if any.</summary>
    public bool? TryGetDirectVerdict(FrameDependency dependency, in ValueHash256 witnessHash, in Eip8288Verifier forkVerifier)
    {
        lock (_gate)
            return _direct.TryGet(new DirectKey(dependency, witnessHash, forkVerifier.SchemeParams(dependency.Scheme)), out bool valid) ? valid : null;
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

    private bool Verify<TKey>(BoundedVerdicts<TKey> verdicts, TKey key, byte[] proof, in ValueHash256 proofHash, Func<bool> verify)
        where TKey : struct, IEquatable<TKey>
    {
        lock (_gate)
            if (verdicts.TryGet(key, out bool known)) return known;
        bool valid = verify();
        // Callers may mutate proof arrays during verification; never record a verdict under stale bytes.
        if (ValueKeccak.Compute(proof) != proofHash) return false;
        lock (_gate) verdicts.Add(key, valid);
        return valid;
    }

    /// <summary>A first-in, first-out bounded verdict table; callers hold the cache lock.</summary>
    private sealed class BoundedVerdicts<TKey> where TKey : struct, IEquatable<TKey>
    {
        private readonly Dictionary<TKey, bool> _verdicts = [];
        private readonly Queue<TKey> _order = new();

        public int Count => _verdicts.Count;

        public bool TryGet(in TKey key, out bool valid) => _verdicts.TryGetValue(key, out valid);

        public void Add(in TKey key, bool valid)
        {
            if (!_verdicts.TryAdd(key, valid)) return;
            _order.Enqueue(key);
            if (_order.Count > Capacity) _verdicts.Remove(_order.Dequeue());
        }
    }
}
