// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.P2P;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>
/// Holds messages for heights above the current one until the chain reaches them, bounded by
/// distance, message count and total bytes.
/// </summary>
/// <remarks>
/// Eviction drops the furthest height first and, when only one height remains, its oldest message.
/// Mirrors Besu's <c>FutureMessageBuffer</c>.
/// </remarks>
public sealed class FutureMessageBuffer(long futureMessagesMaxDistance, long futureMessagesLimit, long chainHeight, long maxTotalMessageBytes = FutureMessageBuffer.DefaultMaxTotalMessageBytes)
{
    public const long DefaultMaxTotalMessageBytes = 64L * 1024 * 1024;

    private readonly SortedDictionary<long, List<QbftReceivedMessage>> _buffer = [];
    private long _chainHeight = chainHeight;

    public long TotalMessageCount
    {
        get
        {
            long count = 0;
            foreach (List<QbftReceivedMessage> messages in _buffer.Values) count += messages.Count;
            return count;
        }
    }

    public long TotalMessageBytes { get; private set; }

    public void AddMessage(long messageChainHeight, QbftReceivedMessage message)
    {
        if (futureMessagesLimit == 0 || !IsValidHeight(messageChainHeight))
        {
            return;
        }

        if (!_buffer.TryGetValue(messageChainHeight, out List<QbftReceivedMessage>? messages))
        {
            messages = [];
            _buffer[messageChainHeight] = messages;
        }

        messages.Add(message);
        TotalMessageBytes += message.Size;

        while (TotalMessageCount > 0 && (TotalMessageCount > futureMessagesLimit || TotalMessageBytes > maxTotalMessageBytes))
        {
            Evict();
        }
    }

    /// <summary>Returns the messages buffered for <paramref name="height"/> and forgets everything at or below it.</summary>
    public List<QbftReceivedMessage> RetrieveMessagesForHeight(long height)
    {
        _chainHeight = height;
        List<QbftReceivedMessage> result = _buffer.TryGetValue(height, out List<QbftReceivedMessage>? messages) ? messages : [];
        List<long> stale = [];
        foreach (long h in _buffer.Keys)
        {
            if (h <= height) stale.Add(h);
            else break;
        }

        foreach (long h in stale)
        {
            Discard(_buffer[h]);
            _buffer.Remove(h);
        }

        return result;
    }

    private bool IsValidHeight(long messageChainHeight) =>
        messageChainHeight > _chainHeight && messageChainHeight <= _chainHeight + futureMessagesMaxDistance;

    private void Evict()
    {
        if (_buffer.Count > 1)
        {
            long lastKey = 0;
            foreach (long key in _buffer.Keys) lastKey = key;
            Discard(_buffer[lastKey]);
            _buffer.Remove(lastKey);
        }
        else if (_buffer.Count == 1)
        {
            foreach (List<QbftReceivedMessage> messages in _buffer.Values)
            {
                if (messages.Count > 0)
                {
                    TotalMessageBytes -= messages[0].Size;
                    messages.RemoveAt(0);
                }
            }
        }
    }

    private void Discard(List<QbftReceivedMessage> messages)
    {
        foreach (QbftReceivedMessage message in messages) TotalMessageBytes -= message.Size;
    }
}
