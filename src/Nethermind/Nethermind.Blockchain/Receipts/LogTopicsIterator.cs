// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts;

/// <summary>Decodes the topics of a stored log one at a time, without allocating them.</summary>
/// <remarks>
/// Rejects the topics in the same cases as <see cref="IReceiptRefDecoder.DecodeTopics"/>, so the last
/// <see cref="TryGetNext"/> also checks that the topics end where their list does.
/// </remarks>
public ref struct LogTopicsIterator
{
    private readonly IReceiptRefDecoder _decoder;
    private readonly int _end;
    private RlpReader _reader;
    private int _index;

    public LogTopicsIterator(ReadOnlySpan<byte> topicsRlp, IReceiptRefDecoder decoder)
    {
        _decoder = decoder;
        _reader = new RlpReader(topicsRlp);
        _end = decoder.StartDecodingTopics(ref _reader);
    }

    /// <param name="topic">Receives the next topic; must be <see cref="Hash256.Size"/> bytes.</param>
    /// <returns><c>false</c> when there are no more topics.</returns>
    public bool TryGetNext(scoped Span<byte> topic)
    {
        if (_reader.Position < _end)
        {
            _decoder.DecodeTopic(ref _reader, _index++, topic);
            return true;
        }

        _reader.Check(_end);
        return false;
    }
}
