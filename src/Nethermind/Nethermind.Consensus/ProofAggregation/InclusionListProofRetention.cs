// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// EIP-8288 inclusion-list proof retention: each proof's bytes are kept, servable with its <c>proof_deps</c>, until the latest
/// deadline the consensus layer supplied for it.
/// </summary>
/// <remarks>
/// <para>A proof-topic submission is retained until its <c>view_freeze_deadline_ms</c>, and a proof answered valid for a list
/// until that call's <c>attestation_deadline_ms</c>, both Unix milliseconds. A later deadline extends an earlier one, so releasing
/// one requirement never drops a proof another still needs, and a deadline already past adds no retention. Evicting a mempool
/// wrapper does not shorten retention: this store holds its own copy. The execution layer derives no deadline itself.</para>
/// <para>Storage is bounded by EIP-8288's peak of <see cref="MaxProofs"/> <c>il_proof</c>s; beyond it the proof whose deadline
/// comes first is dropped.</para>
/// </remarks>
public sealed class InclusionListProofRetention(ITimestamper timestamper)
{
    /// <summary>EIP-8288 peak FOCIL retention: slot <c>N</c>'s 16 committed proofs, slot <c>N + 1</c>'s 32 topic and 16 requested
    /// proofs, and slot <c>N + 2</c>'s 16 early proofs.</summary>
    public const int MaxProofs = 5 * Eip7805Constants.InclusionListCommitteeSize;

    /// <summary>One <c>il_proof</c> at its bounds: <c>MAX_AGGREGATE_PROOF_BYTES</c> (1,310,720) of proof plus
    /// <c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c> 96-byte <c>proof_deps</c> (393,216), 1.625 MiB.</summary>
    public const long MaxProofBytes = Eip8288Constants.MaxAggregateProofBytes
        + (long)Eip8288Constants.MaxDepsPerAggregateWrapper * Eip8288Constants.DependencyTripleLength;

    /// <summary>EIP-8288 peak retention in bytes, 130 MiB.</summary>
    public const long MaxBytes = MaxProofs * MaxProofBytes;

    private readonly Lock _gate = new();
    private readonly Dictionary<(ValueHash256 ProofHash, ValueHash256 DepsHash, ValueHash256 AggregatedVk), Entry> _entries = [];
    private long _bytes;

    private sealed class Entry(byte[] proof, FrameDependency[] proofDeps)
    {
        public byte[] Proof { get; } = proof;
        public FrameDependency[] ProofDeps { get; } = proofDeps;
        public long Size { get; } = proof.Length + (long)proofDeps.Length * Eip8288Constants.DependencyTripleLength;

        /// <summary>The latest retention deadline, in Unix milliseconds.</summary>
        public ulong DeadlineMs { get; set; }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    /// <summary>Keeps a verified proof's bytes and <c>proof_deps</c> until at least <paramref name="deadlineMs"/>.</summary>
    /// <param name="deadlineMs">The consensus-layer deadline in Unix milliseconds; one already past adds no retention.</param>
    public void Retain(byte[] proof, FrameDependency[] proofDeps, in InclusionListCommitment commitment, in Eip8288Verifier verifier,
        ulong deadlineMs)
    {
        (ValueHash256, ValueHash256, ValueHash256) key = (commitment.StarkProofHash, commitment.DepsHash, verifier.AggregatedVk);
        ulong now = NowMs;
        lock (_gate)
        {
            Prune(now);
            if (deadlineMs <= now) return;
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                entry = new((byte[])proof.Clone(), proofDeps);
                _entries.Add(key, entry);
                _bytes += entry.Size;
            }
            entry.DeadlineMs = Math.Max(entry.DeadlineMs, deadlineMs);
            while (_entries.Count > MaxProofs || _bytes > MaxBytes) Evict(key);
        }
    }

    /// <summary>A retained proof under <paramref name="verifier"/>, by <c>stark_proof_hash</c> and, when given, <c>deps_hash</c>.</summary>
    public bool TryGet(in ValueHash256 proofHash, ValueHash256? depsHash, in Eip8288Verifier verifier, out byte[]? proof,
        out FrameDependency[]? proofDeps)
    {
        ulong now = NowMs;
        lock (_gate)
        {
            Prune(now);
            foreach (((ValueHash256 ProofHash, ValueHash256 DepsHash, ValueHash256 AggregatedVk) key, Entry entry) in _entries)
                if (key.ProofHash == proofHash && key.AggregatedVk == verifier.AggregatedVk && (depsHash is null || key.DepsHash == depsHash))
                {
                    proof = entry.Proof;
                    proofDeps = entry.ProofDeps;
                    return true;
                }
        }
        proof = null;
        proofDeps = null;
        return false;
    }

    private ulong NowMs => timestamper.UnixTime.Milliseconds;

    private void Prune(ulong now)
    {
        List<(ValueHash256, ValueHash256, ValueHash256)>? released = null;
        foreach (((ValueHash256, ValueHash256, ValueHash256) key, Entry entry) in _entries)
            if (entry.DeadlineMs <= now) (released ??= []).Add(key);
        foreach ((ValueHash256, ValueHash256, ValueHash256) key in released ?? []) Remove(key);
    }

    /// <summary>Drops the proof whose deadline comes first, sparing <paramref name="keep"/> unless it is the only one.</summary>
    private void Evict((ValueHash256, ValueHash256, ValueHash256) keep)
    {
        (ValueHash256, ValueHash256, ValueHash256)? victim = null;
        ulong earliest = 0;
        foreach (((ValueHash256, ValueHash256, ValueHash256) key, Entry entry) in _entries)
        {
            if (key == keep && _entries.Count > 1) continue;
            // Seeded by the first candidate, so a deadline of ulong.MaxValue is still evictable.
            if (victim is null || entry.DeadlineMs < earliest) { earliest = entry.DeadlineMs; victim = key; }
        }
        if (victim is { } evicted) Remove(evicted);
    }

    private void Remove((ValueHash256, ValueHash256, ValueHash256) key)
    {
        if (_entries.Remove(key, out Entry? entry)) _bytes -= entry.Size;
    }
}
