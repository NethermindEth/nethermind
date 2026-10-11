// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

public sealed partial class LeanObjectTransport
{
    private static readonly TimeSpan SidecarRetry = TimeSpan.FromMilliseconds(250);

    private void OnWrapperValidated(byte[] wrapper, IReadOnlyDictionary<ValueHash256, Transaction> resolved) =>
        PublishInBackground(() => Publish(LeanProtocol.KindWrapper, LeanDescriptor.WrapperContext(), wrapper, null, announce: true, resolved));

    private void OnInclusionListValidated(byte[] package) =>
        PublishInBackground(() => Publish(LeanProtocol.KindInclusionList, LeanDescriptor.InclusionListContext(ValueKeccak.Compute(package)),
            package, null, announce: true));

    private void OnNewHead(object? sender, BlockEventArgs e)
    {
        BlockHeader header = e.Block.Header;
        if (header.RecursiveStark is not null && _specProvider.GetSpec(header).IsEip8288Enabled)
            PublishInBackground(() => PublishBlockProof(header, announce: true));
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
    public bool PublishWrapper(byte[] wrapper) => Publish(LeanProtocol.KindWrapper, LeanDescriptor.WrapperContext(), wrapper, null, announce: true);

    private void PublishBlockProof(BlockHeader header, bool announce)
    {
        RecursiveStark proof = header.RecursiveStark!;
        LeanHeaderSkeleton skeleton = LeanHeaderSkeleton.FromHeader(header, _headerDecoder);
        byte[] context = LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256, header.Number, header.TxRoot!.ValueHash256,
            proof.BlockDepsHash.ValueHash256, skeleton.Hash);
        Publish(LeanProtocol.KindBlockProof, context, LeanBodies.EncodeBlockProof(proof.StarkProof), skeleton, announce);
    }

    /// <summary>Adds a fully validated object to the served store and announces it to peers not known to hold it.</summary>
    /// <param name="resolved">Transactions, by hash, that resolved a kind-1 body's hash entries. A wrapper is offered with hash
    /// entries only while their envelopes stay recoverable through GetTransactions, so each is retained with it.</param>
    private bool Publish(byte kind, byte[] context, byte[] body, LeanHeaderSkeleton? skeleton, bool announce,
        IReadOnlyDictionary<ValueHash256, Transaction>? resolved = null)
    {
        if (!IsEnabled || body.Length == 0 || (ulong)body.Length > LeanLimits.MaxObjectBytes) return false;
        ValueHash256[] transactions = [];
        Dictionary<ValueHash256, byte[]>? envelopes = null;
        if (kind == LeanProtocol.KindWrapper)
        {
            List<LeanBodies.WrapperEntry> entries = LeanBodies.ParseWrapper(body);
            List<ValueHash256> full = new(entries.Count);
            foreach (LeanBodies.WrapperEntry entry in entries)
            {
                if (entry.IsFull) full.Add(entry.Hash);
                else if (RetainableEnvelope(resolved, entry.Hash) is { } envelope) (envelopes ??= [])[entry.Hash] = envelope;
                else return false;
            }
            transactions = [.. full];
        }
        LeanDescriptor descriptor = LeanDescriptor.Create(kind, LocalProfile, context, body, out LeanChunkTree tree);
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed || !_store.TryAdd(new LeanObjectStore.Entry(descriptor, body, tree, skeleton, transactions, envelopes))) return false;
            _tombstones.Remove(descriptor.ObjectId);
            if (announce) AnnounceToPeers(descriptor, outbox);
        }
        outbox.Flush();
        LeanMetrics.Record(kind, LeanEvent.Published);
        if (_logger.IsDebug) _logger.Debug($"lean/1 published {descriptor}");
        return true;
    }

    /// <summary>The canonical envelope of a resolved hash entry, if it fits a Transactions response.</summary>
    /// <remarks>An envelope too large for GetTransactions is recoverable only through a full-entry wrapper, so a wrapper
    /// offering it by hash is not relayed.</remarks>
    private static byte[]? RetainableEnvelope(IReadOnlyDictionary<ValueHash256, Transaction>? resolved, in ValueHash256 hash)
    {
        if (resolved is null || !resolved.TryGetValue(hash, out Transaction? transaction)) return null;
        byte[] envelope = TxDecoder.Instance.Encode(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes;
        return ValueKeccak.Compute(envelope) == hash
            && TransactionsMessageSerializer.EncodeResult(LeanResultStatus.Ok, envelope).Length <= LeanProtocol.MaxTxResponseBytes - ResponseOverhead
            ? envelope : null;
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
        selected.Sort(static (a, b) => a.ObjectId.Bytes.SequenceCompareTo(b.ObjectId.Bytes));
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
        if (block.Hash is not { } hash || block.Header.RecursiveStark is not { } proof) return;
        lock (_gate)
        {
            if (_produced.ContainsKey(hash.ValueHash256)) return;
            if (_producedOrder.Count >= MaxProducedProofs && _producedOrder.TryDequeue(out ValueHash256 oldest)) _produced.Remove(oldest);
            _produced[hash.ValueHash256] = proof;
            _producedOrder.Enqueue(hash.ValueHash256);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Local sources come first: proofs of blocks this node built, served block proofs and known headers. Otherwise peers
    /// that announced the block are asked first, then the rest, until the caller's deadline. The sidecar's context must
    /// match the block's number, transactions root and <c>get_deps_hash(dependencies(block))</c>, and the header rebuilt
    /// from its skeleton must hash to the block hash. No proof is returned for a block whose dependencies use a scheme the
    /// profile does not enable or fail EIP-8288 <c>dependencies_fit_block</c>.
    /// </remarks>
    public async Task<RecursiveStark?> TryGetAsync(Block block, CancellationToken cancellationToken)
    {
        if (block.Hash is not { } hash || block.Header.TxRoot is not { } transactionsRoot) return null;
        List<FrameDependency> dependencies = Eip8288Dependencies.ForBlock(block);
        // Such a block is invalid under the profile's circuit or EIP-8288's block limits, whatever proof a peer offers.
        if (!Eip8288Dependencies.AreSchemesEnabled(dependencies) || !LeanProofCapacity.FitsBlock(dependencies))
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 no sidecar for {block.ToString(Block.Format.Short)}: dependencies outside the profile or block limits");
            return null;
        }
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        LeanSelector selector = new(LeanProtocol.KindBlockProof, LocalProfile, LeanProtocol.LookupPrimary, hash.ValueHash256);
        lock (_gate)
        {
            if (_produced.TryGetValue(hash.ValueHash256, out RecursiveStark? produced)) return produced;
            if (_store.TryLookup(selector, out LeanObjectStore.Entry entry))
                return new RecursiveStark(LeanBodies.ParseBlockProof(entry.Body).ToArray(), new Hash256(entry.Descriptor.BlockDepsHash));
            if (BroadcastSidecar(hash.ValueHash256, block, depsHash) is { } broadcast) return broadcast;
        }
        if (_blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing)
            is { RecursiveStark: { } known }) return known;
        if (!IsEnabled) return null;

        long started = Stopwatch.GetTimestamp();
        HashSet<LeanPeer> asked = [];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                List<LeanPeer> candidates = [];
                Task signal;
                lock (_gate)
                {
                    if (BroadcastSidecar(hash.ValueHash256, block, depsHash) is { } broadcast) return broadcast;
                    signal = _hintSignal.Task;
                    if (_hints.TryGetValue(hash.ValueHash256, out List<LeanPeer>? hinted))
                        foreach (LeanPeer peer in hinted) if (!peer.Closed && !asked.Contains(peer)) candidates.Add(peer);
                    foreach (LeanPeer peer in _peers)
                        if (!asked.Contains(peer) && !candidates.Contains(peer) && peer.Status.SupportsKind(LeanProtocol.KindBlockProof)) candidates.Add(peer);
                }
                foreach (LeanPeer candidate in candidates)
                {
                    asked.Add(candidate);
                    if (await TryFetchSidecarAsync(candidate, selector, block, depsHash, cancellationToken).ConfigureAwait(false) is { } proof)
                    {
                        Interlocked.Increment(ref _stats.SidecarsFetched);
                        LeanMetrics.Record(LeanProtocol.KindBlockProof, LeanEvent.SidecarFetched);
                        LeanMetrics.Transferred(LeanProtocol.KindBlockProof, broadcast: false, Stopwatch.GetElapsedTime(started));
                        if (_logger.IsInfo) _logger.Info($"lean/1 recovered the block proof of {block.ToString(Block.Format.Short)} from {candidate} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:N0} ms");
                        return proof;
                    }
                }
                // Every peer was asked; wait for an announcement or retry, since the producer may still be importing the block.
                asked.Clear();
                await Task.WhenAny(signal, Task.Delay(SidecarRetry, cancellationToken)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        LeanMetrics.Record(LeanProtocol.KindBlockProof, LeanEvent.SidecarUnavailable);
        if (_logger.IsInfo) _logger.Info($"lean/1 block proof of {block.ToString(Block.Format.Short)} unavailable after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:N0} ms");
        return null;
    }

    private async Task<RecursiveStark?> TryFetchSidecarAsync(LeanPeer peer, LeanSelector selector, Block block, ValueHash256 depsHash,
        CancellationToken cancellationToken)
    {
        LeanObjectResult[]? results = await RequestObjectsAsync(peer, [selector], cancellationToken).ConfigureAwait(false);
        if (results?[0] is not { Status: LeanResultStatus.Ok, Descriptor: { } descriptor, Skeleton: { } skeleton }) return null;
        // A self-consistent sidecar for another body is useless here; the payload may be wrong, so the peer is not penalized.
        if (descriptor.BlockNumber != block.Number || descriptor.TransactionsRoot != block.Header.TxRoot!.ValueHash256
            || descriptor.BlockDepsHash != depsHash)
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 sidecar from {peer} does not match block {block.ToString(Block.Format.Short)}");
            return null;
        }
        byte[]? body = await FetchBodyAsync(descriptor, skeleton, peer, cancellationToken).ConfigureAwait(false);
        if (body is null) return null;
        byte[] proof = LeanBodies.ParseBlockProof(body).ToArray();
        if (ValueKeccak.Compute(skeleton.Reconstruct(proof, depsHash)) != selector.LookupKey)
        {
            RejectSidecar(descriptor, peer);
            return null;
        }
        return new RecursiveStark(proof, new Hash256(depsHash));
    }

    private void RejectSidecar(LeanDescriptor descriptor, LeanPeer peer)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            _tombstones.Add(descriptor.ObjectId);
            Interlocked.Increment(ref _stats.Invalid);
            Violation(peer, $"sidecar header does not hash to block {descriptor.BlockHash.ToShortString()}", outbox);
        }
        outbox.Flush();
        LeanMetrics.Record(descriptor.Kind, LeanEvent.Invalid);
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
