// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>Owns completion of one transaction body request against its original tracked state.</summary>
public sealed class BodyRequestAttempt
{
    private readonly Action<BodyRequestAttempt> _sentCallback;
    private readonly Action<BodyRequestAttempt, bool> _completedCallback;
    private int _sent;
    private int _completed;
    private int _omissionConsumed;

    internal BodyRequestAttempt(object owner, object state, ISparseBlobPoolPeer peer, long sequence, Action<BodyRequestAttempt> sent, Action<BodyRequestAttempt, bool> completed)
    {
        Owner = owner;
        State = state;
        Peer = peer;
        Sequence = sequence;
        _sentCallback = sent;
        _completedCallback = completed;
    }

    internal object Owner { get; }
    internal object State { get; }
    internal ISparseBlobPoolPeer Peer { get; }
    internal long Sequence { get; }
    internal bool TryConsumeOmission() => Interlocked.Exchange(ref _omissionConsumed, 1) == 0;

    internal bool IsCompleted => Volatile.Read(ref _completed) != 0;

    internal void ConfirmSent()
    {
        if (Volatile.Read(ref _completed) == 0 && Interlocked.Exchange(ref _sent, 1) == 0) _sentCallback(this);
    }

    internal void Complete(bool unanswered)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) _completedCallback(this, unanswered);
    }
}
