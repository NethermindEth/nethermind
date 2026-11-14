// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Proves held EIP-8437 raw candidates: one leaf per cycle over raw signatures of one recognized program, folded with
/// the candidates' ordinary witnesses into a recursive proof and admitted locally as a mode-1 wrapper, which is then relayed.</summary>
/// <remarks>
/// Opt-in (<see cref="ITxPoolConfig.RawWitnessProverEnabled"/>): a leaf needs gigabytes of memory. A cycle takes the oldest
/// untried candidates whose raw witnesses all name the first one's program, up to
/// <see cref="ITxPoolConfig.RawWitnessMaxClaimsPerLeaf"/> claims, all from one origin (local or one peer) and within the bytes a
/// produced wrapper may carry. Each candidate is tried once; a busy
/// prover returns it untried. The mode-1 wrapper is admitted as its candidates were: a peer's through peer admission, under the
/// first candidate's source, so its transactions never take local treatment.
/// Leaves publish batch statements, so only the batch-pricing guest takes them.
/// </remarks>
public sealed class RawWitnessProver : IDisposable
{
    private readonly ProofWrapperService _wrappers;
    private readonly RawWitnessPool _pool;
    private readonly IRecognizedProgramVerifier _recognized;
    private readonly ILeanProofVerifier _verifier;
    private readonly int _maxClaims;
    private readonly ILogger _logger;
    private readonly ITimer? _timer;
    private int _running;

    public RawWitnessProver(ProofWrapperService wrappers, RawWitnessPool pool, IRecognizedProgramVerifier recognized, ILeanProofVerifier verifier,
        ITxPoolConfig config, ILogManager logManager, TimeProvider? clock = null)
    {
        _wrappers = wrappers;
        _pool = pool;
        _recognized = recognized;
        _verifier = verifier;
        _maxClaims = Math.Clamp(config.RawWitnessMaxClaimsPerLeaf, 1, Eip8288Constants.MaxClaimsPerBatchLeaf);
        _logger = logManager.GetClassLogger<RawWitnessProver>();
        if (!config.RawWitnessProverEnabled || !pool.IsEnabled || !wrappers.IsScheduled) return;
        TimeSpan interval = TimeSpan.FromMilliseconds(Eip8288Constants.AggregationInterval);
        _timer = (clock ?? TimeProvider.System).CreateTimer(static state => ((RawWitnessProver)state!).Tick(), this, interval, interval);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await ProveOnceAsync(); }
            catch (Exception exception) { if (_logger.IsWarn) _logger.Warn($"EIP-8437 raw witness proving failed: {exception.Message}"); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    /// <summary>Proves one group of candidates and admits its mode-1 wrapper.</summary>
    /// <returns>The admission's result, or null when there was nothing to prove.</returns>
    public async Task<ProofWrapperAcceptance?> ProveOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_wrappers.RefreshVerifier() is not { } verifier || verifier.AggregatedVk != Eip8288Verifier.BatchedAggregatedVk) return null;
        List<RawWitnessPool.Entry> group = Select(verifier, out ValueHash256 key);
        if (group.Count == 0) return null;
        foreach (RawWitnessPool.Entry entry in group) entry.Attempted = true;

        List<RawSignature> signatures = [];
        List<FrameDependency> leafDeps = [];
        Dictionary<FrameDependency, byte[]> ordinary = [];
        List<FrameDependency> deps = [];
        Dictionary<ValueHash256, Transaction> transactions = [];
        foreach (RawWitnessPool.Entry entry in group)
        {
            MempoolWrapper wrapper = entry.Decoded;
            // Only the held transactions are proved, so only their dependencies.
            HashSet<FrameDependency> heldDeps = [];
            foreach (Transaction transaction in entry.Held) heldDeps.UnionWith(Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue));
            for (int i = 0; i < wrapper.Deps.Count; i++)
            {
                FrameDependency dependency = wrapper.Deps[i];
                if (!heldDeps.Contains(dependency)) continue;
                deps.Add(dependency);
                if (wrapper.RawWitnesses![i] is { } raw)
                {
                    if (leafDeps.Contains(dependency)) continue;
                    leafDeps.Add(dependency);
                    signatures.Add(raw);
                }
                else ordinary.TryAdd(dependency, wrapper.Proofs![i]);
            }
            foreach (Transaction transaction in entry.Held) transactions.TryAdd(transaction.Hash!.ValueHash256, transaction);
        }
        deps = Eip8288Dependencies.Canonicalize(deps);
        leafDeps = Eip8288Dependencies.Canonicalize(leafDeps);
        // A dependency one candidate carries raw and another as an ordinary witness is established by the leaf alone.
        foreach (FrameDependency dependency in leafDeps) ordinary.Remove(dependency);
        List<ValueHash256> claims = new(leafDeps.Count);
        foreach (FrameDependency dependency in leafDeps) claims.Add(dependency.DataHash);
        List<FrameDependency> ordinaryDeps = Eip8288Dependencies.Canonicalize(ordinary.Keys);
        List<ReadOnlyMemory<byte>> witnesses = new(ordinaryDeps.Count);
        foreach (FrameDependency dependency in ordinaryDeps) witnesses.Add(ordinary[dependency]);

        // Proving work is charged to the candidates' origin before it starts.
        if (!_pool.TryTakeProving(group[0], claims.Count))
        {
            foreach (RawWitnessPool.Entry entry in group) entry.Attempted = false;
            return null;
        }
        ValueHash256 depsHash = verifier.DepsHash(deps);
        byte[] Prove()
        {
            byte[] leaf = _recognized.ProveLeaf(key, signatures);
            AggregationInput input = new()
            {
                Deps = ordinaryDeps,
                Witnesses = witnesses,
                BatchLeaves = [new BatchLeafInput(key, claims, leafDeps, leaf)],
            };
            return _verifier.ProveRecursiveStark(in depsHash, verifier.AggregatedVk.Bytes, input);
        }
        byte[] proof;
        try
        {
            // The leaf and its fold yield to block production, as background aggregation does.
            proof = _verifier is ProductionProofCache cache ? cache.Background(Prove) : Prove();
        }
        catch (Exception exception) when (exception is LeanProverBusyException
            || exception is InvalidOperationException { Message: ProductionProofCache.BackgroundBusyMessage })
        {
            foreach (RawWitnessPool.Entry entry in group) entry.Attempted = false;
            return null;
        }

        List<Transaction> ordered = [.. transactions.Values];
        ordered.Sort(static (a, b) => a.Hash!.CompareTo(b.Hash!));
        byte[] encoded;
        try
        {
            encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
            {
                Transactions = ordered,
                Mode = MempoolWrapper.ModeRecursive,
                Deps = deps,
                RecursiveStark = new RecursiveStark(proof, new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
            }).Bytes;
        }
        catch (RlpException exception)
        {
            if (_logger.IsWarn) _logger.Warn($"EIP-8437 proved raw signatures but could not encode their wrapper: {exception.Message}");
            return null;
        }
        // Proven coverage keeps its candidates' origin: a peer's transactions never become local.
        RawWitnessPool.Entry origin = group[0];
        ProofWrapperAcceptance acceptance = origin.Local
            ? await _wrappers.AcceptDetailedAsync(encoded, cancellationToken)
            : await _wrappers.AcceptOwnedDetailedAsync(encoded, cancellationToken, origin.Source, origin.Identity);
        if (_logger.IsInfo)
            _logger.Info($"EIP-8437 proved {claims.Count} raw signature(s) under {key.ToShortString()} for {ordered.Count} transaction(s): " +
                $"mode-1 wrapper {ValueKeccak.Compute(encoded).ToShortString()} {acceptance.Status}");
        if (acceptance.HasValidProof)
            foreach (RawWitnessPool.Entry entry in group) _pool.Remove(entry.Hash);
        return acceptance;
    }

    /// <summary>The oldest untried candidates under <paramref name="verifier"/> whose raw witnesses all name one recognized program,
    /// up to the claim limit and the step's inner-proof limit.</summary>
    private List<RawWitnessPool.Entry> Select(Eip8288Verifier verifier, out ValueHash256 key)
    {
        key = default;
        List<RawWitnessPool.Entry> group = [];
        HashSet<FrameDependency> raw = [];
        HashSet<FrameDependency> ordinary = [];
        // Each candidate's whole wrapper bounds its transactions' encodings in the produced one.
        long bytes = 0;
        foreach (RawWitnessPool.Entry entry in _pool.Snapshot())
        {
            if (entry.Attempted || entry.Verifier != verifier || group.Count == 0 && !_pool.CanProve(entry, _maxClaims)) continue;
            HashSet<FrameDependency> entryRaw = [];
            ValueHash256? entryKey = null;
            bool sameProgram = true;
            for (int i = 0; i < entry.Decoded.Deps.Count; i++)
            {
                if (entry.Decoded.RawWitnesses![i] is null) continue;
                FrameDependency dependency = entry.Decoded.Deps[i];
                entryKey ??= dependency.VerificationKey;
                sameProgram &= dependency.VerificationKey == entryKey;
                entryRaw.Add(dependency);
            }
            // One program, and one origin, so the proof is admitted as each candidate was.
            if (!sameProgram || entryKey is not { } program || !_recognized.Recognizes(program)
                || group.Count != 0 && (program != key || entry.Local != group[0].Local || !Equals(entry.Identity, group[0].Identity))) continue;
            HashSet<FrameDependency> claims = [.. raw, .. entryRaw];
            HashSet<FrameDependency> direct = [.. ordinary];
            for (int i = 0; i < entry.Decoded.Deps.Count; i++)
                if (entry.Decoded.RawWitnesses![i] is null && entry.Decoded.Deps[i].Scheme == Eip8288Constants.LeanStarkScheme)
                    direct.Add(entry.Decoded.Deps[i]);
            // One leaf beside the direct leanSTARK witnesses, each a singleton leaf of the step.
            if (claims.Count > _maxClaims || direct.Count + 1 > Eip8288Constants.MaxInnerProofsPerStep
                || bytes + entry.Wrapper.Length > ProofWrapperService.MaxWrapperTransactionBytes)
            {
                if (group.Count == 0) entry.Attempted = true;
                break;
            }
            group.Add(entry);
            bytes += entry.Wrapper.Length;
            key = program;
            raw = claims;
            ordinary = direct;
        }
        return group;
    }

    public void Dispose() => _timer?.Dispose();
}
