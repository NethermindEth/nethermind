// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

public sealed partial class LeanObjectTransport
{
    private void OnWrapperValidated(byte[] wrapper, IReadOnlyDictionary<ValueHash256, Transaction> resolved) =>
        _ = Task.Run(() =>
        {
            try { Publish(wrapper, resolved); }
            catch (Exception exception) when (exception is ArgumentException or RlpException or InvalidOperationException)
            {
                if (_logger.IsDebug) _logger.Debug($"lean/1 could not publish a wrapper: {exception.Message}");
            }
        });

    /// <summary>Stores and announces the latest locally aggregated wrapper.</summary>
    public bool PublishWrapper(byte[] wrapper) => Publish(wrapper);

    private bool Publish(byte[] body, IReadOnlyDictionary<ValueHash256, Transaction>? resolved = null)
    {
        if (!IsEnabled || body.Length == 0 || (ulong)body.Length > LeanLimits.MaxObjectBytes) return false;
        Dictionary<ValueHash256, Transaction> envelopes = [];
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
        LeanDescriptor descriptor = LeanDescriptor.Create(LocalProfile, body);
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed || !_store.TryAdd(new(descriptor, (byte[])body.Clone(), envelopes))) return false;
            _tombstones.Remove(descriptor.ObjectId);
            AnnounceToPeers(descriptor, outbox);
        }
        outbox.Flush();
        LeanMetrics.Record(LeanEvent.Published);
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
            if (descriptor.ByteLength > peer.Status.MaxObjectBytes || peer.Known.Contains(descriptor.ObjectId, now)) continue;
            peer.Known.Add(descriptor.ObjectId);
            outbox.Send(peer, new AnnounceObjectsMessage([descriptor]));
            Interlocked.Increment(ref _stats.AnnouncedOut);
            LeanMetrics.Record(LeanEvent.Announced);
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
            if (descriptor.ByteLength > peer.Status.MaxObjectBytes || peer.Known.Contains(descriptor.ObjectId, now)) continue;
            selected.Add(descriptor);
            if (selected.Count == LeanProtocol.MaxAnnouncements) break;
        }
        if (selected.Count == 0) return;
        selected.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));
        foreach (LeanDescriptor descriptor in selected)
        {
            peer.Known.Add(descriptor.ObjectId);
            LeanMetrics.Record(LeanEvent.Announced);
        }
        Interlocked.Add(ref _stats.AnnouncedOut, selected.Count);
        outbox.Send(peer, new AnnounceObjectsMessage([.. selected]));
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
        if (announcedIn + announcedOut + fetched + chunksIn + chunksOut + violations == 0) return;
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
            $"local failures {localFailures}, expired {expired}, chunks in {chunksIn} out {chunksOut}, objects served {served}, violations {violations}");
    }
}
