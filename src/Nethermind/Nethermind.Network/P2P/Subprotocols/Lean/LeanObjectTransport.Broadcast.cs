// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Whether an authenticated broadcast manifest may start or join a session.</summary>
internal enum LeanBroadcastAdmission
{
    /// <summary>Coded state is charged and the shared assembly exists.</summary>
    Started,

    /// <summary>The object is already held and validated, so its shards can be served from the body.</summary>
    Held,

    /// <summary>The object is known invalid or the transport is inactive.</summary>
    Rejected,

    /// <summary>Local budgets are exhausted; not peer misconduct.</summary>
    Busy
}

public sealed partial class LeanObjectTransport
{
    private const int MaxBroadcastSidecars = 16;
    private const int MaxBroadcastProgress = 256;

    /// <summary>Bounds of the wait for a new broadcast shard, about one round trip, before retrieving the object instead.</summary>
    private static readonly TimeSpan MinBroadcastDeferral = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxBroadcastDeferral = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRoundTrip = TimeSpan.FromMilliseconds(200);

    /// <summary>Retrieval waits at most this many round trips after a manifest is accepted, however often shards arrive.</summary>
    private const int MaxBroadcastDeferrals = 3;

    private readonly Dictionary<ValueHash256, BroadcastProofCandidate> _broadcastSidecars = [];
    private readonly Queue<ValueHash256> _broadcastSidecarOrder = [];

    /// <summary>When each object's manifest was accepted and when its broadcast last made progress, keyed by
    /// <see cref="BroadcastKey"/>; kept until its session ends, so a later shard cannot restart the deferral.</summary>
    private readonly Dictionary<ValueHash256, (long Accepted, long Last)> _broadcastProgress = [];

    /// <summary>The lowest peer round trip, in the clock's timestamp ticks; zero until measured.</summary>
    private long _roundTrip;

    /// <summary>When the transport timer next fires, in timestamp ticks.</summary>
    private long _nextTick = long.MaxValue;

    /// <summary>The chain ID and genesis hash that scope manifest IDs and channels, or null while the genesis is unknown.</summary>
    internal (UInt256 ChainId, ValueHash256 Genesis)? BroadcastScope => CreateStatus() is { } status ? (status.ChainId, status.GenesisHash) : null;

    /// <summary>The existing descriptor, known-header and applicable-fork checks a manifest must pass before it is accepted.</summary>
    internal string? CheckBroadcastDescriptor(LeanDescriptor descriptor)
    {
        if (descriptor.ProfileId != LocalProfile) return "profile is not common";
        if ((LocalKinds & (1 << (descriptor.Kind - 1))) == 0) return "kind is not supported";
        if (descriptor.ByteLength > LeanLimits.MaxObjectBytes) return "object exceeds max_object_bytes";
        if (descriptor.Kind != LeanProtocol.KindBlockProof) return "only block proofs are broadcast";
        if (_blockTree.FindHeader(new Hash256(descriptor.BlockHash), BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing)
            is not { } header) return "block header is unknown";
        if (!_specProvider.GetSpec(header).IsEip8288Enabled) return "profile is not active for the block";
        if (header.RecursiveStark is null) return "block has no proof commitment";
        return null;
    }

    /// <summary>Charges a broadcast's coded state and starts the object's shared assembly if none exists.</summary>
    /// <remarks>EIP-8437: all bindings and manifests contribute to one assembly per object ID with one absolute deadline;
    /// requested retrieval can recover the object within it if the broadcast fails.</remarks>
    internal LeanBroadcastAdmission AdmitBroadcast(LeanDescriptor descriptor, long codedBytes, LeanPeer? source = null)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_disposed || !IsEnabled || _tombstones.Contains(descriptor.ObjectId, now)) return LeanBroadcastAdmission.Rejected;
            bool held = _store.Contains(descriptor.ObjectId);
            if (codedBytes > LeanLimits.MaxIncompleteBytes - _incompleteBytes - _completionBytes
                || (source is not null && codedBytes > LeanLimits.MaxPeerBytes - source.ChargedBytes)) return LeanBroadcastAdmission.Busy;
            if (!held && !_assemblies.ContainsKey(descriptor.ObjectId))
            {
                if (TryCreateAssembly(descriptor, source, now) is not { } assembly) return LeanBroadcastAdmission.Busy;
                assembly.Validate = false;
            }
            if (codedBytes > LeanLimits.MaxIncompleteBytes - _incompleteBytes - _completionBytes
                || (source is not null && codedBytes > LeanLimits.MaxPeerBytes - source.ChargedBytes)) return LeanBroadcastAdmission.Busy;
            _incompleteBytes += codedBytes;
            if (source is not null) source.ChargedBytes += codedBytes;
            return held ? LeanBroadcastAdmission.Held : LeanBroadcastAdmission.Started;
        }
    }

    /// <summary>Records an accepted manifest or a new checked shard of its object.</summary>
    /// <remarks>EIP-8437: while new checked shards arrive, chunk requests for the object wait; retrieval resumes once none has
    /// arrived for about one round trip, and at most <see cref="MaxBroadcastDeferrals"/> round trips after the manifest was
    /// accepted. Deferral changes no assembly or request deadline.</remarks>
    internal void OnBroadcastProgress(LeanDescriptor descriptor)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            ValueHash256 key = BroadcastKey(descriptor);
            if (_broadcastProgress.TryGetValue(key, out (long Accepted, long Last) progress))
            {
                _broadcastProgress[key] = (progress.Accepted, now);
                return;
            }
            if (_broadcastProgress.Count >= MaxBroadcastProgress) PruneBroadcastProgress(now);
            if (_broadcastProgress.Count < MaxBroadcastProgress) _broadcastProgress[key] = (now, now);
        }
    }

    /// <summary>Ends the deferral of an object's retrieval once its broadcast session failed or expired.</summary>
    /// <remarks>Called under the engine's lock, so nothing is sent here: the next tick schedules the object's requests.</remarks>
    internal void OnBroadcastEnded(LeanDescriptor descriptor)
    {
        lock (_gate) _broadcastProgress.Remove(BroadcastKey(descriptor));
    }

    /// <summary>Kind 2 is keyed by block hash, which is what a sidecar lookup knows; other kinds by object ID.</summary>
    private static ValueHash256 BroadcastKey(LeanDescriptor descriptor) =>
        descriptor.Kind == LeanProtocol.KindBlockProof ? descriptor.BlockHash : descriptor.ObjectId;

    /// <summary>How much longer retrieval waits for the broadcast covering <paramref name="key"/>; zero when none is progressing.</summary>
    private TimeSpan BroadcastDeferral(in ValueHash256 key, long now)
    {
        if (_broadcastProgress.Count == 0 || !_broadcastProgress.TryGetValue(key, out (long Accepted, long Last) progress)) return TimeSpan.Zero;
        TimeSpan wait = BroadcastWait;
        TimeSpan remaining = Min(wait - _clock.GetElapsedTime(progress.Last, now),
            MaxBroadcastDeferrals * wait - _clock.GetElapsedTime(progress.Accepted, now));
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;

        static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    }

    /// <summary>The lowest peer round trip within <see cref="MinBroadcastDeferral"/> and <see cref="MaxBroadcastDeferral"/>.</summary>
    /// <remarks>The lowest, so a slow or stalling peer cannot lengthen the wait, and no peer can shorten it below its real round trip.</remarks>
    internal TimeSpan BroadcastWait
    {
        get
        {
            if (_roundTrip == 0) return DefaultRoundTrip;
            TimeSpan roundTrip = _clock.GetElapsedTime(0, _roundTrip);
            return roundTrip < MinBroadcastDeferral ? MinBroadcastDeferral : roundTrip > MaxBroadcastDeferral ? MaxBroadcastDeferral : roundTrip;
        }
    }

    /// <summary>Drops the oldest progress entries, which outlived any session that could still deliver shards.</summary>
    private void PruneBroadcastProgress(long now)
    {
        List<ValueHash256>? stale = null;
        foreach ((ValueHash256 key, (long accepted, _)) in _broadcastProgress)
            if (_clock.GetElapsedTime(accepted, now) >= LeanProtocol.MaxAssemblyAge) (stale ??= []).Add(key);
        if (stale is not null) foreach (ValueHash256 key in stale) _broadcastProgress.Remove(key);
    }

    /// <summary>Folds the time from sending a chunk request to its first chunk into the peer's round trip.</summary>
    internal void SampleRoundTrip(LeanPeer peer, long elapsed)
    {
        if (elapsed <= 0) return;
        peer.RoundTrip = peer.RoundTrip == 0 ? elapsed : peer.RoundTrip + (elapsed - peer.RoundTrip) / 8;
        UpdateRoundTrip();
    }

    /// <summary>Recomputes the lowest round trip over connected peers.</summary>
    private void UpdateRoundTrip()
    {
        long lowest = 0;
        foreach (LeanPeer peer in _peers)
            if (peer.RoundTrip > 0 && (lowest == 0 || peer.RoundTrip < lowest)) lowest = peer.RoundTrip;
        _roundTrip = lowest;
    }

    /// <summary>Brings the timer forward to when a deferral ends, so retrieval resumes then rather than at the next tick.</summary>
    private void WakeAfter(TimeSpan delay, long now)
    {
        long wake = now + (long)(delay.TotalSeconds * _clock.TimestampFrequency);
        if (wake >= _nextTick) return;
        _nextTick = wake;
        _timer.Change(delay, LeanLimits.Tick);
    }

    internal DateTimeOffset BroadcastDeadline(in ValueHash256 objectId)
    {
        lock (_gate)
        {
            TimeSpan remaining = _assemblies.TryGetValue(objectId, out LeanAssembly? assembly)
                ? LeanProtocol.MaxAssemblyAge - _clock.GetElapsedTime(assembly.Created) : LeanProtocol.MaxAssemblyAge;
            return _clock.GetUtcNow() + remaining;
        }
    }

    internal void ReleaseBroadcast(long codedBytes, LeanPeer? source = null)
    {
        lock (_gate)
        {
            _incompleteBytes -= codedBytes;
            if (source is not null) { source.ChargedBytes -= codedBytes; PruneNode(source.Node); }
        }
    }

    /// <summary>The body of a held, fully validated object, so a node that already has it can serve its shards.</summary>
    internal byte[]? HeldBody(in ValueHash256 objectId)
    {
        lock (_gate) return _store.TryGet(objectId, out LeanObjectStore.Entry entry) ? entry.Body : null;
    }

    /// <summary>Checks a reconstructed block-proof body's canonical commitments against its known ordinary header.</summary>
    /// <remarks>Dependency and native verification still run when the importing block obtains this proof.</remarks>
    internal string? AcceptBroadcastBody(LeanDescriptor descriptor, byte[] body, long started, LeanPeer? source = null)
    {
        if ((ulong)body.Length != descriptor.ByteLength) return "reconstructed length differs from the descriptor";
        if (ValueKeccak.Compute(body) != descriptor.ContentHash) return "content hash mismatch";
        try { LeanBodies.Check(descriptor.Kind, body); }
        catch (RlpException exception)
        {
            string error = $"noncanonical body: {exception.Message}";
            RejectBroadcastObject(descriptor, error);
            return error;
        }
        if (CheckBroadcastDescriptor(descriptor) is not null) return null;
        ReadOnlySpan<byte> proof = LeanBodies.ParseBlockProof(body);
        BlockHeader? header = _blockTree.FindHeader(new Hash256(descriptor.BlockHash),
            BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (header?.RecursiveStark?.StarkProofHash.ValueHash256 != ValueKeccak.Compute(proof))
        {
            const string error = "proof differs from the block header commitment";
            RejectBroadcastObject(descriptor, error);
            return error;
        }
        Outbox outbox = new(_clock);
        lock (_gate)
        {
            if (_disposed) return null;
            if (_broadcastSidecars.ContainsKey(descriptor.BlockHash)) return null;
            if (proof.Length > LeanLimits.MaxIncompleteBytes - _incompleteBytes - _completionBytes
                || (source is not null && proof.Length > LeanLimits.MaxPeerBytes - source.ChargedBytes)) return null;
            if (_broadcastSidecarOrder.Count >= MaxBroadcastSidecars && _broadcastSidecarOrder.TryDequeue(out ValueHash256 oldest))
                ReleaseBroadcastSidecar(oldest);
            _broadcastSidecarOrder.Enqueue(descriptor.BlockHash);
            long created = _assemblies.TryGetValue(descriptor.ObjectId, out LeanAssembly? assembly) ? assembly.Created : _clock.GetTimestamp();
            _incompleteBytes += proof.Length;
            if (source is not null) source.ChargedBytes += proof.Length;
            _broadcastSidecars.Add(descriptor.BlockHash, new BroadcastProofCandidate(this, descriptor, proof.ToArray(), source, created));
            TaskCompletionSource signal = _hintSignal;
            _hintSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
            if (assembly is not null && !assembly.Queued)
            {
                CancelRequestsFor(assembly, outbox);
                _assemblies.Remove(descriptor.ObjectId);
                Drop(assembly);
            }
        }
        outbox.Flush();
        LeanMetrics.Transferred(LeanProtocol.KindBlockProof, broadcast: true, Stopwatch.GetElapsedTime(started));
        return null;
    }

    /// <summary>Tombstones a committed broadcast object that is invalid, as retrieval would; the signer is at fault, so no relay
    /// that forwarded its checked shards is penalized.</summary>
    private void RejectBroadcastObject(LeanDescriptor descriptor, string reason)
    {
        Outbox outbox = new(_clock);
        lock (_gate)
        {
            _tombstones.Add(descriptor.ObjectId);
            Interlocked.Increment(ref _stats.Invalid);
            if (_assemblies.TryGetValue(descriptor.ObjectId, out LeanAssembly? assembly) && !assembly.Queued)
            {
                CancelRequestsFor(assembly, outbox);
                _assemblies.Remove(descriptor.ObjectId);
                Drop(assembly);
            }
        }
        outbox.Flush();
        LeanMetrics.Record(descriptor.Kind, LeanEvent.Invalid);
        if (_logger.IsInfo) _logger.Info($"lean/1 rejected broadcast {descriptor}: {reason}");
    }

    internal bool HasBroadcastSidecar(in ValueHash256 blockHash) { lock (_gate) return _broadcastSidecars.ContainsKey(blockHash); }

    /// <summary>Takes a candidate whose byte lease stays charged until full block-proof authentication finishes.</summary>
    private BroadcastProofCandidate? TakeBroadcastSidecar(in ValueHash256 blockHash)
    {
        lock (_gate) return _broadcastSidecars.Remove(blockHash, out BroadcastProofCandidate? candidate) ? candidate : null;
    }

    private void ReleaseBroadcastSidecar(in ValueHash256 blockHash)
    {
        if (_broadcastSidecars.Remove(blockHash, out BroadcastProofCandidate? candidate)) candidate.Dispose();
    }

    internal void ExpireBroadcastSidecars()
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            List<ValueHash256>? expired = null;
            foreach ((ValueHash256 hash, BroadcastProofCandidate sidecar) in _broadcastSidecars)
                if (_disposed || _clock.GetElapsedTime(sidecar.Created, now) >= LeanProtocol.MaxAssemblyAge) (expired ??= []).Add(hash);
            if (expired is not null) foreach (ValueHash256 hash in expired) ReleaseBroadcastSidecar(hash);
        }
    }

    private void DisposeBroadcastSidecars()
    {
        foreach (BroadcastProofCandidate sidecar in _broadcastSidecars.Values) sidecar.Dispose();
        _broadcastSidecars.Clear();
        _broadcastSidecarOrder.Clear();
    }

    private sealed class BroadcastProofCandidate(LeanObjectTransport owner, LeanDescriptor descriptor, byte[] proof, LeanPeer? source, long created) : IDisposable
    {
        private int _disposed;
        public LeanDescriptor Descriptor { get; } = descriptor;
        public byte[] Proof { get; } = proof;
        public long Created { get; } = created;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.ReleaseBroadcast(Proof.Length, source);
        }
    }
}
