// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>What an incoming ethp2p response stream prefix names.</summary>
internal enum LeanResponseTarget
{
    /// <summary>Request ID zero or above the highest issued ID: a protocol violation.</summary>
    Invalid,

    /// <summary>An issued request that is no longer live: the stream is discarded without payload work.</summary>
    Retired,

    /// <summary>A live request that already has a response stream: a protocol violation.</summary>
    Duplicate,

    GetObjects,
    GetChunks,
    GetTransactions
}

public sealed partial class LeanObjectTransport
{
    /// <summary>Resolves the request named by a new response stream and marks it as having one.</summary>
    internal LeanResponseTarget OpenResponseStream(LeanPeer peer, ulong requestId)
    {
        lock (_gate)
        {
            if (requestId == 0 || requestId > peer.LastIssued) return LeanResponseTarget.Invalid;
            if (peer.Closed || !peer.Live.TryGetValue(requestId, out LeanOutgoingRequest? request)) return LeanResponseTarget.Retired;
            if (request.Streamed) return LeanResponseTarget.Duplicate;
            request.Streamed = true;
            return request switch
            {
                LeanObjectsRequest => LeanResponseTarget.GetObjects,
                LeanChunksRequest => LeanResponseTarget.GetChunks,
                _ => LeanResponseTarget.GetTransactions
            };
        }
    }

    internal bool IsLive(LeanPeer peer, ulong requestId)
    {
        lock (_gate) return !peer.Closed && peer.Live.ContainsKey(requestId);
    }

    /// <summary>Retires a request whose response stream was reset or finished before its terminal response.</summary>
    /// <remarks>
    /// EIP-8437: this is incomplete delivery, never Served, and not misconduct; chunks already verified stay retained and
    /// the indices in flight go to other sources after a short throttle, as for Busy.
    /// </remarks>
    internal void OnResponseAborted(LeanPeer peer, ulong requestId)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (peer.Closed || !peer.Live.Remove(requestId, out LeanOutgoingRequest? request)) return;
            long now = _clock.GetTimestamp();
            if (!request.Cancelled)
            {
                Fail(request, peer);
                peer.ThrottledUntil = now + (long)(LeanLimits.BusyRetry.TotalSeconds * _clock.TimestampFrequency);
            }
            Schedule(outbox, now);
        }
        outbox.Flush();
    }
}
