// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Identifies the type of an <see cref="IndexEntry"/>.
/// </summary>
/// <remarks>
/// EIP-8304 defines seven entry types (type IDs 0–6). The two-byte big-endian
/// type ID is the first field in every binary-encoded entry and drives both
/// encoding length and lexicographic sort order.
/// </remarks>
public enum IndexEntryType : ushort
{
    Block = 0,
    Transaction = 1,
    LogAddress = 2,
    LogTopic0 = 3,
    LogTopic1 = 4,
    LogTopic2 = 5,
    LogTopic3 = 6,
}

/// <summary>
/// A single index entry for the EIP-8304 trustless log and transaction index.
/// </summary>
/// <remarks>
/// Each entry has a two-byte big-endian type ID, a variable-length content field
/// (block hash, tx hash, address, or topic), and position information (block number,
/// optional tx index, optional log index or cumulative log count). Entries are
/// lexicographically ordered by their binary representations, which the big-endian
/// encoding naturally provides.
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
public readonly struct IndexEntry : IComparable<IndexEntry>
{
    /// <summary>Maximum encoded length across all entry types (transaction and log topic entries).</summary>
    public const int MaxEncodedLength = 50;

    private const int TypeIdLength = 2;
    private const int HashLength = 32;
    private const int AddressLength = 20;
    private const int BlockNumberLength = 8;
    private const int UInt32Length = 4;

    private const int BlockEncodedLength = TypeIdLength + HashLength + BlockNumberLength; // 42
    private const int TransactionEncodedLength = TypeIdLength + HashLength + BlockNumberLength + UInt32Length + UInt32Length; // 50
    private const int LogAddressEncodedLength = TypeIdLength + AddressLength + BlockNumberLength + UInt32Length + UInt32Length; // 38
    private const int LogTopicEncodedLength = TypeIdLength + HashLength + BlockNumberLength + UInt32Length + UInt32Length; // 50

    private readonly IndexEntryType _type;
    private readonly byte[] _content; // 32 bytes (hash/topic) or 20 bytes (address)
    private readonly ulong _blockNumber;
    private readonly uint _field1; // tx index (for tx/log entries) or 0
    private readonly uint _field2; // cumulative log count (tx) or log index (log entries) or 0

    private IndexEntry(IndexEntryType type, byte[] content, ulong blockNumber, uint field1, uint field2)
    {
        _type = type;
        _content = content;
        _blockNumber = blockNumber;
        _field1 = field1;
        _field2 = field2;
    }

    /// <summary>The entry type.</summary>
    public IndexEntryType Type => _type;

    /// <summary>The block number recorded in the position info.</summary>
    public ulong BlockNumber => _blockNumber;

    /// <summary>
    /// The encoded length of this entry in bytes.
    /// </summary>
    public int EncodedLength => _type switch
    {
        IndexEntryType.Block => BlockEncodedLength,
        IndexEntryType.Transaction => TransactionEncodedLength,
        IndexEntryType.LogAddress => LogAddressEncodedLength,
        _ => LogTopicEncodedLength,
    };

    /// <summary>
    /// Creates a block index entry.
    /// </summary>
    /// <remarks>
    /// EIP-8304: block entries record the hash of a block with a one-block delay.
    /// Block N's table contains the block entry for block N−1.
    /// </remarks>
    /// <param name="blockHash">The hash of the block being indexed.</param>
    /// <param name="blockNumber">The block number whose hash is recorded.</param>
    public static IndexEntry CreateBlock(Hash256 blockHash, ulong blockNumber) =>
        new(IndexEntryType.Block, blockHash.BytesToArray(), blockNumber, 0, 0);

    /// <summary>
    /// Creates a transaction index entry.
    /// </summary>
    /// <param name="txHash">The transaction hash.</param>
    /// <param name="blockNumber">The block number containing the transaction.</param>
    /// <param name="txIndex">The transaction's index within the block.</param>
    /// <param name="cumulativeLogCount">Total number of logs in the block before this transaction.</param>
    public static IndexEntry CreateTransaction(Hash256 txHash, ulong blockNumber, uint txIndex, uint cumulativeLogCount) =>
        new(IndexEntryType.Transaction, txHash.BytesToArray(), blockNumber, txIndex, cumulativeLogCount);

    /// <summary>
    /// Creates a log address index entry.
    /// </summary>
    /// <param name="address">The address that emitted the log.</param>
    /// <param name="blockNumber">The block number containing the log.</param>
    /// <param name="txIndex">The transaction index within the block.</param>
    /// <param name="logIndex">The log index relative to the transaction beginning.</param>
    public static IndexEntry CreateLogAddress(Address address, ulong blockNumber, uint txIndex, uint logIndex) =>
        new(IndexEntryType.LogAddress, address.Bytes.ToArray(), blockNumber, txIndex, logIndex);

    /// <summary>
    /// Creates a log topic index entry.
    /// </summary>
    /// <param name="topicIndex">The topic position (0–3), mapping to type IDs 3–6.</param>
    /// <param name="topic">The topic value.</param>
    /// <param name="blockNumber">The block number containing the log.</param>
    /// <param name="txIndex">The transaction index within the block.</param>
    /// <param name="logIndex">The log index relative to the transaction beginning.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="topicIndex"/> is not 0–3.</exception>
    public static IndexEntry CreateLogTopic(int topicIndex, Hash256 topic, ulong blockNumber, uint txIndex, uint logIndex)
    {
        if ((uint)topicIndex > 3)
            throw new ArgumentOutOfRangeException(nameof(topicIndex), topicIndex, "Topic index must be 0–3.");

        IndexEntryType type = (IndexEntryType)(3 + topicIndex);
        return new IndexEntry(type, topic.BytesToArray(), blockNumber, txIndex, logIndex);
    }

    /// <summary>
    /// Encodes this entry into the destination buffer using EIP-8304 binary format.
    /// </summary>
    /// <param name="destination">Buffer that must be at least <see cref="EncodedLength"/> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Encode(Span<byte> destination)
    {
        int length = EncodedLength;
        int offset = 0;

        // Type ID (2 bytes, big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)_type);
        offset += TypeIdLength;

        // Content (hash or address bytes)
        _content.AsSpan().CopyTo(destination[offset..]);
        offset += _content.Length;

        // Block number (8 bytes, big-endian)
        BinaryPrimitives.WriteUInt64BigEndian(destination[offset..], _blockNumber);
        offset += BlockNumberLength;

        // Position fields (only for non-block entries)
        if (_type is not IndexEntryType.Block)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], _field1);
            offset += UInt32Length;

            BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], _field2);
            offset += UInt32Length;
        }

        return length;
    }

    /// <summary>
    /// Compares two entries by lexicographic ordering of their binary encodings.
    /// </summary>
    /// <remarks>
    /// EIP-8304 specifies that entries are ordered by their binary representation.
    /// Big-endian encoding ensures that numeric comparison matches byte comparison,
    /// so we compare type → content → position fields sequentially.
    /// </remarks>
    public int CompareTo(IndexEntry other)
    {
        int cmp = ((ushort)_type).CompareTo((ushort)other._type);
        if (cmp != 0) return cmp;

        cmp = _content.AsSpan().SequenceCompareTo(other._content.AsSpan());
        if (cmp != 0) return cmp;

        cmp = _blockNumber.CompareTo(other._blockNumber);
        if (cmp != 0) return cmp;

        if (_type is not IndexEntryType.Block)
        {
            cmp = _field1.CompareTo(other._field1);
            if (cmp != 0) return cmp;

            cmp = _field2.CompareTo(other._field2);
        }

        return cmp;
    }
}
