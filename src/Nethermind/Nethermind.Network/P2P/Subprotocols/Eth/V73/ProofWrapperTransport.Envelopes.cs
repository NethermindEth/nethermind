// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

public sealed partial class ProofWrapperTransport
{
    internal int RetainedEnvelopeCount { get { lock (_gate) return _retained.Count; } }
    internal bool IsRetained(in ValueHash256 hash) { lock (_gate) return _retained.Contains(hash); }

    /// <summary>Retains a dependency-bearing envelope a peer pushed through Transactions or PooledTransactions until a covering
    /// wrapper arrives, within finite byte, count and time limits.</summary>
    /// <remarks>
    /// EIP-8437 lets envelopes propagate alongside wrapper announcements, but the transaction is neither admitted nor relayed
    /// until a covering wrapper is fully validated; validation then uses the retained envelope in place of
    /// GetPooledTransactions. An envelope that expires with no covering wrapper announced on its connection lowers the
    /// sender's score (<see cref="ExpireRetained"/>).
    /// </remarks>
    internal void RetainEnvelope(WrapperPeer peer, Transaction transaction)
    {
        if (!IsEnabled) return;
        byte[] envelope = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
        ValueHash256 hash = ValueKeccak.Compute(envelope);
        if (_txPool.TryGetPendingTransaction(hash, out _)) return;
        bool retained;
        lock (_gate)
        {
            if (_disposed || peer.Closed || _store.TryGetEnvelope(hash, out _) || _retained.Contains(hash)) return;
            long now = _clock.GetTimestamp();
            retained = peer.RetainedEnvelopes < WrapperTransportLimits.MaxRetainedEnvelopesPerPeer
                && _retained.TryAdd(new(hash, envelope, peer, now));
            // A full cache is this node's limit, not the sender's fault.
            if (!retained) peer.ScoreExemptAt = now;
        }
        if (retained) WrapperMetrics.Record(WrapperEvent.EnvelopeRetained);
    }

    /// <summary>Drops envelopes past the retention window, lowering the sender's score for each one no covering wrapper
    /// followed on that connection.</summary>
    /// <remarks>
    /// EIP-8437 attributes the omission only when the sender's own wrapper announcements are all resolved: an envelope whose
    /// sender still has an announcement pending, or whose announcements were left unresolved or refused under this node's
    /// limits since the envelope arrived, lowers no score.
    /// </remarks>
    private void ExpireRetained(long now, Outbox outbox)
    {
        long cutoff = now - (long)(WrapperTransportLimits.EnvelopeRetention.TotalSeconds * _clock.TimestampFrequency);
        List<RetainedEnvelopes.Item>? expired = _retained.RemoveReceivedBy(cutoff);
        if (expired is null) return;
        HashSet<WrapperPeer>? pending = null;
        foreach (WrapperFetch fetch in _fetches.Values)
            foreach (WrapperPeer source in fetch.Sources) (pending ??= []).Add(source);
        foreach (RetainedEnvelopes.Item item in expired)
        {
            WrapperPeer from = item.From;
            if (from.Closed || pending?.Contains(from) == true || from.ScoreExemptAt >= item.Received
                || CoveredOnConnection(from, item.Hash, now)) continue;
            WrapperMetrics.Record(WrapperEvent.EnvelopeUncovered);
            outbox.LowerScore(from, $"eth/73: envelope {item.Hash.ToShortString()} pushed without a covering wrapper");
        }
    }

    /// <summary>Whether a held wrapper exchanged with <paramref name="peer"/> lists <paramref name="hash"/>.</summary>
    private bool CoveredOnConnection(WrapperPeer peer, in ValueHash256 hash, long now)
    {
        foreach (ValueHash256 wrapperHash in _store.Listing(hash))
            if (peer.Exchanged.Contains(wrapperHash, now)) return true;
        return false;
    }
}
