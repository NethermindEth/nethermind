// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Whether an authenticated broadcast manifest may start or join a session.</summary>
internal enum LeanBroadcastAdmission
{
    /// <summary>Coded state is charged and, for kind 3, the shared assembly exists.</summary>
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

    private readonly Dictionary<ValueHash256, (LeanDescriptor Descriptor, byte[] Proof)> _broadcastSidecars = [];
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

    /// <summary>The existing descriptor, skeleton and applicable-fork checks a manifest must pass before it is accepted.</summary>
    internal string? CheckBroadcastDescriptor(LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton)
    {
        if (descriptor.ProfileId != LocalProfile) return "profile is not common";
        if ((LocalKinds & (1 << (descriptor.Kind - 1))) == 0) return "kind is not supported";
        if (descriptor.ByteLength > LeanLimits.MaxObjectBytes) return "object exceeds max_object_bytes";
        if (descriptor.Kind == LeanProtocol.KindBlockProof
            && (skeleton is null || !skeleton.TryValidate(descriptor, _headerDecoder, _specProvider, out string? error))) return "invalid header skeleton";
        return null;
    }

    /// <summary>Charges a broadcast's coded state and, for kind 3, starts the object's shared assembly if none exists.</summary>
    /// <remarks>EIP-8437: all bindings and manifests contribute to one assembly per object ID with one absolute deadline;
    /// requested retrieval can recover the object within it if the broadcast fails.</remarks>
    internal LeanBroadcastAdmission AdmitBroadcast(LeanDescriptor descriptor, long codedBytes)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_disposed || !IsEnabled || _tombstones.Contains(descriptor.ObjectId, now)) return LeanBroadcastAdmission.Rejected;
            if (_store.Contains(descriptor.ObjectId)) return LeanBroadcastAdmission.Held;
            if (codedBytes > LeanLimits.MaxIncompleteBytes - _incompleteBytes) return LeanBroadcastAdmission.Busy;
            if (descriptor.Kind == LeanProtocol.KindInclusionList && !_assemblies.ContainsKey(descriptor.ObjectId))
            {
                if (TryCreateAssembly(descriptor, null, null, now) is not { } assembly) return LeanBroadcastAdmission.Busy;
                assembly.Validate = true;
            }
            _incompleteBytes += codedBytes;
            return LeanBroadcastAdmission.Started;
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

    internal void ReleaseBroadcast(long codedBytes)
    {
        lock (_gate) _incompleteBytes -= codedBytes;
    }

    /// <summary>The body of a held, fully validated object, so a node that already has it can serve its shards.</summary>
    internal byte[]? HeldBody(in ValueHash256 objectId)
    {
        lock (_gate) return _store.TryGet(objectId, out LeanObjectStore.Entry entry) ? entry.Body : null;
    }

    /// <summary>Applies the canonical object checks to a body reconstructed from broadcast shards, then validates it.</summary>
    /// <param name="started">The <see cref="Stopwatch"/> timestamp at which the broadcast session was accepted.</param>
    /// <returns>Null when the body enters validation or is refused locally; otherwise the producer fault. A body that is not
    /// the one the descriptor commits to, or does not rebuild its block's header, fails only the manifest; a committed body
    /// that fails kind validation is invalid exactly as if retrieved.</returns>
    /// <remarks>
    /// A kind-3 package completes its shared assembly and goes through the same validation queue as retrieved objects, so an
    /// invalid package, including a noncanonical encoding, is rejected as such. A kind-2 proof that rebuilds the header
    /// committed by the block hash is kept for block import, which still checks it against the block's dependencies. The
    /// first such proof for a block is kept.
    /// </remarks>
    internal string? AcceptBroadcastBody(LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton, byte[] body, long started)
    {
        if (CheckCommitments(descriptor, body) is { } commitmentError) return commitmentError;
        if (descriptor.Kind == LeanProtocol.KindBlockProof)
        {
            List<LeanBodies.WrapperEntry>? entries = null;
            if (CheckEncoding(descriptor, body, ref entries) is { } encodingError)
            {
                RejectBroadcastObject(descriptor, encodingError);
                return encodingError;
            }
            byte[] proof = LeanBodies.ParseBlockProof(body).ToArray();
            if (ValueKeccak.Compute(skeleton!.Reconstruct(proof, descriptor.BlockDepsHash)) != descriptor.BlockHash)
                return "proof does not rebuild the header of its block hash";
            lock (_gate)
            {
                // The first valid sidecar for a block is kept; a later one cannot replace it.
                if (_broadcastSidecars.ContainsKey(descriptor.BlockHash)) return null;
                if (_broadcastSidecarOrder.Count >= MaxBroadcastSidecars && _broadcastSidecarOrder.TryDequeue(out ValueHash256 oldest))
                    _broadcastSidecars.Remove(oldest);
                _broadcastSidecarOrder.Enqueue(descriptor.BlockHash);
                _broadcastSidecars.Add(descriptor.BlockHash, (descriptor, proof));
                TaskCompletionSource signal = _hintSignal;
                _hintSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                signal.TrySetResult();
            }
            LeanMetrics.Transferred(LeanProtocol.KindBlockProof, broadcast: true, Stopwatch.GetElapsedTime(started));
            return null;
        }
        return QueueBroadcastPackage(descriptor, body, started);
    }

    /// <summary>Tombstones a committed broadcast object that is invalid, as retrieval would; the signer is at fault, so no relay
    /// that forwarded its checked shards is penalized.</summary>
    private void RejectBroadcastObject(LeanDescriptor descriptor, string reason)
    {
        lock (_gate)
        {
            _tombstones.Add(descriptor.ObjectId);
            Interlocked.Increment(ref _stats.Invalid);
        }
        LeanMetrics.Record(descriptor.Kind, LeanEvent.Invalid);
        if (_logger.IsInfo) _logger.Info($"lean/1 rejected broadcast {descriptor}: {reason}");
    }

    /// <summary>Completes the package's shared assembly with the reconstructed body, so it is validated once, in the bounded queue.</summary>
    /// <remarks>Requested retrieval of the same object stops; a body retrieval already completed is validated instead.</remarks>
    private string? QueueBroadcastPackage(LeanDescriptor descriptor, byte[] body, long started)
    {
        Outbox outbox = new(_clock);
        lock (_gate)
        {
            if (_disposed || _store.Contains(descriptor.ObjectId)) return null;
            long now = _clock.GetTimestamp();
            if (!_assemblies.TryGetValue(descriptor.ObjectId, out LeanAssembly? assembly))
            {
                if (_tombstones.Contains(descriptor.ObjectId, now)) return null;
                assembly = TryCreateAssembly(descriptor, null, null, now);
                if (assembly is null) return LocalRefusal("no room for its assembly");
                assembly.Validate = true;
            }
            if (assembly.Queued || assembly.Dropped) return null;
            long length = (long)descriptor.ByteLength;
            if (_pendingValidations >= LeanLimits.MaxPendingValidations || length > LeanLimits.MaxCompletionBytes - _completionBytes)
                return LocalRefusal("validation queue full");
            _completionBytes += length;
            assembly.Validate = true;
            assembly.BroadcastStarted = started;
            Enqueue(assembly, body, outbox);
        }
        outbox.Flush();
        return null;
    }

    private string? LocalRefusal(string reason)
    {
        // Not a producer fault: the assembly, if any, stays available to requested retrieval.
        if (_logger.IsDebug) _logger.Debug($"lean/1 broadcast body not queued: {reason}");
        return null;
    }

    internal bool HasBroadcastSidecar(in ValueHash256 blockHash) { lock (_gate) return _broadcastSidecars.ContainsKey(blockHash); }

    /// <summary>A broadcast sidecar matching the importing block's number, transactions root and dependency hash.</summary>
    private RecursiveStark? BroadcastSidecar(in ValueHash256 blockHash, Block block, in ValueHash256 depsHash) =>
        _broadcastSidecars.TryGetValue(blockHash, out (LeanDescriptor Descriptor, byte[] Proof) sidecar)
        && sidecar.Descriptor.BlockNumber == block.Number && sidecar.Descriptor.TransactionsRoot == block.Header.TxRoot!.ValueHash256
        && sidecar.Descriptor.BlockDepsHash == depsHash
            ? new RecursiveStark(sidecar.Proof, new Hash256(depsHash))
            : null;
}
