// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// EIP-8288 inclusion-list proof retention: each proof is kept, servable with its <c>proof_deps</c>, while any slot's retention
/// requirement on it applies.
/// </summary>
/// <remarks>
/// <para>For the lists of slot <c>N</c>, whose target block of slot <c>N + 1</c> has timestamp <c>T</c>: a proof answered
/// <see cref="Requirement.Valid"/> for a list is kept through the EIP-7805 attestation deadline of slot <c>N + 1</c>, and a
/// proof-topic submission (<see cref="Requirement.Topic"/>) until the view freeze deadline of slot <c>N</c>, when its
/// requirement is released unless it was also answered valid for a list. Releasing one slot's requirement never deletes a
/// proof another slot still needs, and evicting a mempool wrapper does not shorten retention: this store holds its own copy.</para>
/// <para>The deadlines are EIP-7805's <c>t=9s</c> and <c>t=4s</c> of a 12-second slot, scaled to the configured slot length.
/// Storage is bounded by EIP-8288's peak of <see cref="MaxProofs"/> <c>il_proof</c>s; beyond it the proof whose requirements
/// end first is dropped.</para>
/// </remarks>
public sealed class InclusionListProofRetention(ITimestamper timestamper, ulong secondsPerSlot)
{
    /// <summary>EIP-8288 peak FOCIL retention: slot <c>N</c>'s 16 committed proofs, slot <c>N + 1</c>'s 32 topic and 16 requested
    /// proofs, and slot <c>N + 2</c>'s 16 early proofs.</summary>
    public const int MaxProofs = 5 * Eip7805Constants.InclusionListCommitteeSize;

    /// <summary>One <c>il_proof</c> at its bounds: <c>MAX_AGGREGATE_PROOF_BYTES</c> plus <c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c>
    /// 96-byte dependencies, 1.625 MiB.</summary>
    public const long MaxProofBytes = Eip8288Constants.MaxAggregateProofBytes
        + (long)Eip8288Constants.MaxDepsPerAggregateWrapper * Eip8288Constants.DependencyTripleLength;

    /// <summary>EIP-8288 peak retention in bytes, 130 MiB.</summary>
    public const long MaxBytes = MaxProofs * MaxProofBytes;

    /// <summary>Why a slot retains a proof.</summary>
    public enum Requirement
    {
        /// <summary>Submitted from the proof topic; released at the view freeze deadline of the list slot.</summary>
        Topic,

        /// <summary>Answered valid for a list; kept through the attestation deadline of the target slot.</summary>
        Valid
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<(ValueHash256 ProofHash, ValueHash256 DepsHash, ValueHash256 AggregatedVk), Entry> _entries = [];
    private long _bytes;

    private sealed class Entry(byte[] proof, FrameDependency[] proofDeps)
    {
        public byte[] Proof { get; } = proof;
        public FrameDependency[] ProofDeps { get; } = proofDeps;
        public long Size { get; } = proof.Length + (long)proofDeps.Length * Eip8288Constants.DependencyTripleLength;

        /// <summary>The strongest requirement per target-block timestamp.</summary>
        public Dictionary<ulong, Requirement> Requirements { get; } = [];
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    /// <summary>Adds a <paramref name="requirement"/> of the slot whose target block has <paramref name="targetTimestamp"/>.</summary>
    public void Retain(byte[] proof, FrameDependency[] proofDeps, in InclusionListCommitment commitment, in Eip8288Verifier verifier,
        ulong targetTimestamp, Requirement requirement)
    {
        (ValueHash256, ValueHash256, ValueHash256) key = (commitment.StarkProofHash, commitment.DepsHash, verifier.AggregatedVk);
        ulong now = Now;
        lock (_gate)
        {
            Prune(now);
            if (Expiry(targetTimestamp, requirement) <= now) return;
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                entry = new((byte[])proof.Clone(), proofDeps);
                _entries.Add(key, entry);
                _bytes += entry.Size;
            }
            if (!entry.Requirements.TryGetValue(targetTimestamp, out Requirement existing) || existing < requirement)
                entry.Requirements[targetTimestamp] = requirement;
            while (_entries.Count > MaxProofs || _bytes > MaxBytes) Evict(key);
        }
    }

    /// <summary>A retained proof under <paramref name="verifier"/>, by <c>stark_proof_hash</c> and, when given, <c>deps_hash</c>.</summary>
    public bool TryGet(in ValueHash256 proofHash, ValueHash256? depsHash, in Eip8288Verifier verifier, out byte[]? proof,
        out FrameDependency[]? proofDeps)
    {
        ulong now = Now;
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

    private ulong Now => (ulong)timestamper.UnixTime.Seconds;

    /// <summary>When a slot's requirement ends: the view freeze deadline of the list slot for a topic proof, the attestation
    /// deadline of the target slot for a valid one.</summary>
    private ulong Expiry(ulong targetTimestamp, Requirement requirement) => requirement == Requirement.Valid
        ? targetTimestamp + secondsPerSlot / 3
        : targetTimestamp - Math.Min(targetTimestamp, secondsPerSlot / 4);

    private ulong LatestExpiry(Entry entry)
    {
        ulong latest = 0;
        foreach ((ulong timestamp, Requirement requirement) in entry.Requirements) latest = Math.Max(latest, Expiry(timestamp, requirement));
        return latest;
    }

    private void Prune(ulong now)
    {
        List<(ValueHash256, ValueHash256, ValueHash256)>? released = null;
        foreach (((ValueHash256, ValueHash256, ValueHash256) key, Entry entry) in _entries)
        {
            List<ulong>? ended = null;
            foreach ((ulong timestamp, Requirement requirement) in entry.Requirements)
                if (Expiry(timestamp, requirement) <= now) (ended ??= []).Add(timestamp);
            foreach (ulong timestamp in ended ?? []) entry.Requirements.Remove(timestamp);
            if (entry.Requirements.Count == 0) (released ??= []).Add(key);
        }
        foreach ((ValueHash256, ValueHash256, ValueHash256) key in released ?? []) Remove(key);
    }

    /// <summary>Drops the proof whose requirements end first, sparing <paramref name="keep"/> unless it is the only one.</summary>
    private void Evict((ValueHash256, ValueHash256, ValueHash256) keep)
    {
        (ValueHash256, ValueHash256, ValueHash256)? victim = null;
        ulong earliest = ulong.MaxValue;
        foreach (((ValueHash256, ValueHash256, ValueHash256) key, Entry entry) in _entries)
        {
            if (key == keep && _entries.Count > 1) continue;
            ulong expiry = LatestExpiry(entry);
            if (expiry < earliest) { earliest = expiry; victim = key; }
        }
        if (victim is { } evicted) Remove(evicted);
    }

    private void Remove((ValueHash256, ValueHash256, ValueHash256) key)
    {
        if (_entries.Remove(key, out Entry? entry)) _bytes -= entry.Size;
    }
}
