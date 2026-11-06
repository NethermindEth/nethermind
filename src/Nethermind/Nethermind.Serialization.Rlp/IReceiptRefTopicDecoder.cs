// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

/// <summary>A receipt decoder that can also decode the topics of a log one at a time, without allocating them.</summary>
/// <remarks>
/// Optional, so that a decoder that implements only <see cref="IReceiptRefDecoder"/> keeps working: its topics are
/// decoded with <see cref="IReceiptRefDecoder.DecodeTopics"/> instead.
/// </remarks>
public interface IReceiptRefTopicDecoder : IReceiptRefDecoder
{
    /// <summary>Reads a log's topics sequence, to decode its topics one at a time with <see cref="DecodeTopic"/>.</summary>
    /// <remarks>
    /// The caller decodes while the returned reader has items, as <c>LogTopicsIterator</c> does, so the topics are
    /// rejected in the same cases as by <see cref="IReceiptRefDecoder.DecodeTopics"/>.
    /// </remarks>
    /// <returns>A reader over the topics.</returns>
    RlpReader StartDecodingTopics(scoped ref RlpReader reader);

    /// <summary>Decodes the next topic without allocating it.</summary>
    /// <param name="reader">The reader that <see cref="StartDecodingTopics"/> returned.</param>
    /// <param name="index">The position of the topic in the log, used only in an error message.</param>
    /// <param name="topic">Receives the topic; must be <see cref="Hash256.Size"/> bytes.</param>
    void DecodeTopic(ref RlpReader reader, int index, scoped Span<byte> topic);
}
