// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

/// <summary>Decodes the topics of a stored log one at a time, without allocating them.</summary>
/// <remarks>Rejects the topics in the same cases as <see cref="IReceiptRefDecoder.DecodeTopics"/>.</remarks>
public ref struct LogTopicsIterator
{
    private readonly IReceiptRefTopicDecoder _decoder;
    private RlpReader _topics;
    private int _index;

    public LogTopicsIterator(ReadOnlySpan<byte> topicsRlp, IReceiptRefTopicDecoder decoder)
    {
        _decoder = decoder;
        RlpReader reader = new(topicsRlp);
        _topics = decoder.StartDecodingTopics(ref reader);
    }

    /// <param name="topic">Receives the next topic; must be <see cref="Hash256.Size"/> bytes.</param>
    /// <returns><c>false</c> when there are no more topics.</returns>
    public bool TryGetNext(scoped Span<byte> topic)
    {
        if (!_topics.HasNext) return false;

        _decoder.DecodeTopic(ref _topics, _index++, topic);
        return true;
    }
}
