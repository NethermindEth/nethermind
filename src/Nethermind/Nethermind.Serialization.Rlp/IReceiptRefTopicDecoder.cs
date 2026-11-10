// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

/// <summary>A receipt decoder that can also decode the topics of a log one at a time, without allocating them.</summary>
/// <remarks>
/// Optional, so that a decoder that implements only <see cref="IReceiptRefDecoder"/> keeps working: its topics are
/// decoded with <see cref="IReceiptRefDecoder.DecodeTopics"/> instead.
/// </remarks>
public interface IReceiptRefTopicDecoder : IReceiptRefDecoder
{
    /// <summary>Reads the start of a log's topics, to decode them one at a time with <see cref="DecodeTopic"/>.</summary>
    /// <remarks>
    /// The caller decodes while <see cref="RlpReader.Position"/> is before the returned position and then checks that it
    /// is at it, as <c>LogTopicsIterator</c> does, so the topics are rejected in the same cases as by <see cref="IReceiptRefDecoder.DecodeTopics"/>.
    /// </remarks>
    /// <returns>The position past the topics.</returns>
    int StartDecodingTopics(ref RlpReader reader);

    /// <summary>Decodes the next topic without allocating it.</summary>
    /// <param name="index">The position of the topic in the log, used only in an error message.</param>
    /// <param name="topic">Receives the topic; must be <see cref="Hash256.Size"/> bytes.</param>
    void DecodeTopic(ref RlpReader reader, int index, scoped Span<byte> topic);
}
