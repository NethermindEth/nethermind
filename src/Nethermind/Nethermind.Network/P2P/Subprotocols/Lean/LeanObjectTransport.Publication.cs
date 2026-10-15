// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

public sealed partial class LeanObjectTransport
{
    private static readonly TimeSpan SidecarRetry = TimeSpan.FromMilliseconds(250);

    private void OnWrapperValidated(byte[] wrapper, IReadOnlyDictionary<ValueHash256, Transaction> resolved) =>
        PublishInBackground(() => Publish(LeanProtocol.KindWrapper, LeanDescriptor.WrapperContext(), wrapper, announce: true, resolved));

    private void OnNewHead(object? sender, BlockEventArgs e)
    {
        BlockHeader header = e.Block.Header;
        if (header.RecursiveStark is not null && e.Block.StarkProof is not null && _specProvider.GetSpec(header).IsEip8288Enabled)
            PublishInBackground(() => PublishBlockProof(e.Block, announce: true));
    }

    private void PublishInBackground(Action publish) => _ = Task.Run(() =>
    {
        try { publish(); }
        catch (Exception exception) when (exception is ArgumentException or RlpException or InvalidOperationException)
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 could not publish an object: {exception.Message}");
        }
    });

    /// <summary>Stores and announces the latest locally aggregated wrapper.</summary>
    public bool PublishWrapper(byte[] wrapper) => Publish(LeanProtocol.KindWrapper, LeanDescriptor.WrapperContext(), wrapper, announce: true);

    private void PublishBlockProof(Block block, bool announce)
    {
        if (block.Header.RecursiveStark is null || block.StarkProof is not { } proof || block.Hash is null
            || ValueKeccak.Compute(proof) != block.Header.RecursiveStark.StarkProofHash.ValueHash256) return;
        Publish(LeanProtocol.KindBlockProof, LeanDescriptor.BlockProofContext(block.Hash.ValueHash256),
            LeanBodies.EncodeBlockProof(proof), announce);
    }

    private bool Publish(byte kind, byte[] context, byte[] body, bool announce,
        IReadOnlyDictionary<ValueHash256, Transaction>? resolved = null)
    {
        if (!IsEnabled || body.Length == 0 || (ulong)body.Length > LeanLimits.MaxObjectBytes) return false;
        Dictionary<ValueHash256, Transaction>? envelopes = null;
        if (kind == LeanProtocol.KindWrapper)
        {
            envelopes = [];
            long envelopeBytes = body.Length + 256;
            foreach (ValueHash256 hash in LeanBodies.ParseWrapper(body))
            {
                if (resolved?.TryGetValue(hash, out Transaction? transaction) != true && !_txPool.TryGetPendingTransaction(hash, out transaction)) return false;
                if ((transaction!.Hash ?? transaction.CalculateHash()).ValueHash256 != hash) return false;
                // Store ownership cannot follow callers mutating a resolved transaction after publication.
                envelopeBytes += TxDecoder.Instance.GetLength(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping) + 128;
                if (envelopeBytes > LeanLimits.MaxStoreBytes) return false;
                byte[] encoded = TxDecoder.Instance.Encode(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes;
                envelopes.Add(hash, TxDecoder.Instance.DecodeCompleteNotNull(encoded, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping));
            }
        }
        LeanDescriptor descriptor = LeanDescriptor.Create(kind, LocalProfile, context, body);
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed || !_store.TryAdd(new(descriptor, (byte[])body.Clone(), envelopes))) return false;
            _tombstones.Remove(descriptor.ObjectId);
            if (announce) AnnounceToPeers(descriptor, outbox);
        }
        outbox.Flush();
        LeanMetrics.Record(kind, LeanEvent.Published);
        if (_logger.IsDebug) _logger.Debug($"lean/1 published {descriptor}");
        return true;
    }

    internal bool TryGetRetainedTransaction(in ValueHash256 hash, out Transaction? transaction)
    {
        lock (_gate)
        {
            if (_store.TryGetEnvelope(hash, out Transaction retained))
            {
                transaction = retained;
                return true;
            }
        }
        transaction = null;
        return false;
    }

    private void AnnounceToPeers(LeanDescriptor descriptor, Outbox outbox)
    {
        long now = _clock.GetTimestamp();
        foreach (LeanPeer peer in _peers)
        {
            if (!peer.Status.SupportsKind(descriptor.Kind) || descriptor.ByteLength > peer.Status.MaxObjectBytes
                || peer.Known.Contains(descriptor.ObjectId, now)) continue;
            peer.Known.Add(descriptor.ObjectId);
            outbox.Send(peer, new AnnounceObjectsMessage([descriptor]));
            Interlocked.Increment(ref _stats.AnnouncedOut);
            LeanMetrics.Record(descriptor.Kind, LeanEvent.Announced);
        }
    }

    /// <summary>Offers a newly connected peer the most recent stored objects, in one bounded announcement.</summary>
    private void AnnounceStored(LeanPeer peer, Outbox outbox)
    {
        long now = _clock.GetTimestamp();
        List<LeanDescriptor> selected = [];
        foreach (LeanObjectStore.Entry entry in _store.Recent())
        {
            LeanDescriptor descriptor = entry.Descriptor;
            if (!peer.Status.SupportsKind(descriptor.Kind) || descriptor.ByteLength > peer.Status.MaxObjectBytes
                || peer.Known.Contains(descriptor.ObjectId, now)) continue;
            selected.Add(descriptor);
            if (selected.Count == LeanProtocol.MaxAnnouncements) break;
        }
        if (selected.Count == 0) return;
        selected.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));
        foreach (LeanDescriptor descriptor in selected)
        {
            peer.Known.Add(descriptor.ObjectId);
            LeanMetrics.Record(descriptor.Kind, LeanEvent.Announced);
        }
        Interlocked.Add(ref _stats.AnnouncedOut, selected.Count);
        outbox.Send(peer, new AnnounceObjectsMessage([.. selected]));
    }

    /// <inheritdoc/>
    public void RememberProduced(Block block)
    {
        if (block.Hash is not { } hash || block.Header.RecursiveStark is not { } commitment || block.StarkProof is not { } proof) return;
        lock (_gate)
        {
            if (_produced.ContainsKey(hash.ValueHash256)) return;
            if (_producedOrder.Count >= MaxProducedProofs && _producedOrder.TryDequeue(out ValueHash256 oldest)) _produced.Remove(oldest);
            _produced[hash.ValueHash256] = new((byte[])proof.Clone(), commitment.BlockDepsHash);
            _producedOrder.Enqueue(hash.ValueHash256);
        }
    }

    private bool IsValidBlockProof(Block block, ReadOnlySpan<byte> proof) => CheckBlockProof(block, proof) == Verdict.Valid;

    private Verdict CheckBlockProof(Block block, ReadOnlySpan<byte> proof)
    {
        if (block.Header.RecursiveStark is not { } commitment || proof.Length > Eip8288Constants.MaxAggregateProofBytes
            || ValueKeccak.Compute(proof) != commitment.StarkProofHash.ValueHash256) return Verdict.Invalid;
        if (!_specProvider.GetSpec(block.Header).IsEip8288Enabled) return Verdict.Refused;
        List<FrameDependency> dependencies = Eip8288Dependencies.ForBlock(block);
        ValueHash256 dependencyHash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        if (!Eip8288Dependencies.AreSchemesEnabled(dependencies) || !LeanProofCapacity.FitsBlock(dependencies)
            || dependencyHash != commitment.BlockDepsHash.ValueHash256) return Verdict.Invalid;
        if (LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(dependencies)) is not null) return Verdict.Refused;
        try
        {
            // The native bool verifier also returns false for a missing library: distinguish local availability first.
            _proofVerifier.EnsureAvailable();
            return _proofVerifier.VerifyRecursiveStark(in dependencyHash, Eip8288Constants.AggregatedVk, proof) ? Verdict.Valid : Verdict.Invalid;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 block-proof verification unavailable: {exception.Message}");
            return Verdict.LocalFailure;
        }
    }

    /// <inheritdoc/>
    public async Task<RecursiveStark?> TryGetAsync(Block block, CancellationToken cancellationToken)
    {
        if (block.Hash is not { } hash || block.Header.RecursiveStark is not { } commitment) return null;
        if (block.StarkProof is { } supplied)
            return IsValidBlockProof(block, supplied) ? new((byte[])supplied.Clone(), commitment.BlockDepsHash) : null;
        LeanSelector selector = new(LeanProtocol.KindBlockProof, LocalProfile, hash.ValueHash256);
        byte[]? local = null;
        lock (_gate)
        {
            if (_produced.TryGetValue(hash.ValueHash256, out RecursiveStark? produced)) local = produced.StarkProof;
            else if (_store.TryLookup(selector, out LeanObjectStore.Entry entry)) local = LeanBodies.ParseBlockProof(entry.Body).ToArray();
        }
        if (local is not null && IsValidBlockProof(block, local)) return new((byte[])local.Clone(), commitment.BlockDepsHash);
        if (!IsEnabled) return null;
        HashSet<LeanPeer> asked = [];
        while (!cancellationToken.IsCancellationRequested)
        {
            LeanPeer[] candidates;
            Task signal;
            lock (_gate)
            {
                signal = _hintSignal.Task;
                List<LeanPeer> available = [];
                if (_hints.TryGetValue(hash.ValueHash256, out List<LeanPeer>? hinted))
                    foreach (LeanPeer peer in hinted) if (!peer.Closed && !asked.Contains(peer)) available.Add(peer);
                foreach (LeanPeer peer in _peers)
                    if (!peer.Closed && !asked.Contains(peer) && !available.Contains(peer)
                        && peer.Status.SupportsKind(LeanProtocol.KindBlockProof)) available.Add(peer);
                candidates = [.. available];
            }
            foreach (LeanPeer peer in candidates)
            {
                asked.Add(peer);
                LeanObjectResult[]? results = await RequestObjectsAsync(peer, [selector], cancellationToken).ConfigureAwait(false);
                if (results?[0] is not { Status: LeanResultStatus.Ok, Descriptor: { } descriptor }) continue;
                using LeanFetchedBody? fetched = await FetchBodyAsync(descriptor, peer, cancellationToken).ConfigureAwait(false);
                if (fetched is null) continue;
                ReadOnlySpan<byte> proof = LeanBodies.ParseBlockProof(fetched.Body);
                Verdict verdict = CheckBlockProof(block, proof);
                if (verdict != Verdict.Valid)
                {
                    if (verdict == Verdict.Invalid)
                    {
                        Outbox outbox = new();
                        lock (_gate) Violation(peer, "Block proof differs from its header or dependency statement", outbox);
                        outbox.Flush();
                    }
                    continue;
                }
                Interlocked.Increment(ref _stats.SidecarsFetched);
                return new(proof.ToArray(), commitment.BlockDepsHash);
            }
            if (asked.Count >= LeanLimits.MaxSourcesPerAssembly) return null;
            try { await Task.WhenAny(signal, Task.Delay(SidecarRetry, cancellationToken)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        }
        return null;
    }

    private void LogStats(long now)
    {
        if (!_logger.IsInfo || _clock.GetElapsedTime(Volatile.Read(ref _lastStats), now) < StatsInterval) return;
        Volatile.Write(ref _lastStats, now);
        LeanStats stats = _stats;
        long announcedIn = Interlocked.Exchange(ref stats.AnnouncedIn, 0);
        long announcedOut = Interlocked.Exchange(ref stats.AnnouncedOut, 0);
        long fetched = Interlocked.Exchange(ref stats.Fetched, 0);
        long reassembled = Interlocked.Exchange(ref stats.Reassembled, 0);
        long validated = Interlocked.Exchange(ref stats.Validated, 0);
        long invalid = Interlocked.Exchange(ref stats.Invalid, 0);
        long localFailures = Interlocked.Exchange(ref stats.LocalFailures, 0);
        long expired = Interlocked.Exchange(ref stats.Expired, 0);
        long chunksIn = Interlocked.Exchange(ref stats.ChunksReceived, 0);
        long chunksOut = Interlocked.Exchange(ref stats.ChunksServed, 0);
        long served = Interlocked.Exchange(ref stats.ObjectsServed, 0);
        long violations = Interlocked.Exchange(ref stats.Violations, 0);
        long sidecars = Interlocked.Exchange(ref stats.SidecarsFetched, 0);
        if (announcedIn + announcedOut + fetched + chunksIn + chunksOut + violations + sidecars == 0) return;
        int peers, stored, assemblies;
        long storedBytes;
        lock (_gate)
        {
            peers = _peers.Count;
            stored = _store.Count;
            storedBytes = _store.Bytes;
            assemblies = _assemblies.Count;
        }
        _logger.Info($"lean/1 | {peers} peers, {stored} objects stored ({storedBytes / (1024 * 1024)} MiB), {assemblies} assemblies | last {StatsInterval.TotalSeconds:N0}s: " +
            $"announced in {announcedIn} out {announcedOut}, fetched {fetched}, reassembled {reassembled}, validated {validated}, invalid {invalid}, " +
            $"local failures {localFailures}, expired {expired}, chunks in {chunksIn} out {chunksOut}, objects served {served}, sidecars {sidecars}, violations {violations}");
    }
}
