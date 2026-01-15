// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Interface for RLP reading operations, enabling both real decoding
/// and counting-only validation passes to prevent memory DOS attacks.
/// </summary>
public interface IRlpReader
{
    /// <summary>
    /// Current position in the RLP stream.
    /// </summary>
    int Position { get; set; }

    /// <summary>
    /// Total length of the RLP data.
    /// </summary>
    int Length { get; }

    /// <summary>
    /// Reads the sequence length prefix and returns the content length.
    /// </summary>
    int ReadSequenceLength();

    /// <summary>
    /// Skips the current item without decoding it.
    /// </summary>
    void SkipItem();

    /// <summary>
    /// Peeks the byte at current position without advancing.
    /// </summary>
    byte PeekByte();

    /// <summary>
    /// Counts items remaining in the current sequence without advancing position.
    /// </summary>
    int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue);

    /// <summary>
    /// Decodes a long integer.
    /// </summary>
    long DecodeLong();

    /// <summary>
    /// Decodes an unsigned long integer.
    /// </summary>
    ulong DecodeULong();

    /// <summary>
    /// Decodes an integer.
    /// </summary>
    int DecodeInt();

    /// <summary>
    /// Decodes a single byte.
    /// </summary>
    byte DecodeByte();

    /// <summary>
    /// Decodes a byte array.
    /// </summary>
    byte[] DecodeByteArray(RlpLimit? limit = null);

    /// <summary>
    /// Decodes a byte array and returns it as a span.
    /// </summary>
    ReadOnlySpan<byte> DecodeByteArraySpan(RlpLimit? limit = null);

    /// <summary>
    /// Decodes a Keccak256 hash.
    /// </summary>
    Hash256? DecodeKeccak();

    /// <summary>
    /// Decodes an Ethereum address.
    /// </summary>
    Address? DecodeAddress();

    /// <summary>
    /// Decodes a UInt256 value.
    /// </summary>
    UInt256 DecodeUInt256(int length = -1);

    /// <summary>
    /// Decodes an array of items using the provided decode function.
    /// </summary>
    T[] DecodeArray<T>(
        Func<IRlpReader, T> decodeItem,
        bool checkPositions = true,
        T defaultElement = default,
        RlpLimit? limit = null);

    /// <summary>
    /// Decodes an array of items into an ArrayPoolList using the provided decode function.
    /// </summary>
    ArrayPoolList<T> DecodeArrayPoolList<T>(
        Func<IRlpReader, T> decodeItem,
        bool checkPositions = true,
        T defaultElement = default,
        RlpLimit? limit = null);
}
