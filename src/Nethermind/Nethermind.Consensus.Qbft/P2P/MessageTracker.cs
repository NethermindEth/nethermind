// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.P2P;

/// <summary>Remembers the keccak of the last <c>N</c> messages seen, evicting the oldest first.</summary>
public sealed class MessageTracker(int messageTrackingLimit)
{
    private readonly HashSet<ValueHash256> _seen = [];
    private readonly Queue<ValueHash256> _order = new();
    private readonly Lock _lock = new();

    public void AddSeenMessage(ReadOnlySpan<byte> data)
    {
        ValueHash256 id = ValueKeccak.Compute(data);
        lock (_lock)
        {
            if (!_seen.Add(id))
            {
                return;
            }

            _order.Enqueue(id);
            while (_order.Count > messageTrackingLimit)
            {
                _seen.Remove(_order.Dequeue());
            }
        }
    }

    public bool HasSeenMessage(ReadOnlySpan<byte> data)
    {
        ValueHash256 id = ValueKeccak.Compute(data);
        lock (_lock)
        {
            return _seen.Contains(id);
        }
    }
}

/// <summary>Sends each distinct message at most once; wraps the multicaster used for both gossip and own messages.</summary>
public sealed class UniqueMessageMulticaster(IValidatorMulticaster multicaster, MessageTracker gossipedMessageTracker) : IValidatorMulticaster
{
    public UniqueMessageMulticaster(IValidatorMulticaster multicaster, int gossipHistoryLimit) : this(multicaster, new MessageTracker(gossipHistoryLimit)) { }

    public void Send(int code, byte[] data) => Send(code, data, []);

    public void Send(int code, byte[] data, IReadOnlyCollection<Core.Address> denylist)
    {
        if (gossipedMessageTracker.HasSeenMessage(data))
        {
            return;
        }

        multicaster.Send(code, data, denylist);
        gossipedMessageTracker.AddSeenMessage(data);
    }
}

/// <summary>Relays a validator's message to the other validators, skipping the peer it came from and its author.</summary>
public interface IQbftGossiper
{
    void Send(QbftReceivedMessage message, Core.Address author);
}

public sealed class QbftGossiper(IValidatorMulticaster multicaster) : IQbftGossiper
{
    public void Send(QbftReceivedMessage message, Core.Address author) =>
        multicaster.Send(message.Code, message.Data, [message.SenderAddress, author]);
}
