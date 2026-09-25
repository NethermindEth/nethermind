// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>The resume position of a paged <c>get_logs</c> query, carried to the client as an opaque string.</summary>
/// <remarks>
/// <para>
/// A page resumes at the first matching log of <see cref="Block"/> whose block-level log index is at least
/// <see cref="LogIndex"/>. Logs are returned in (block, log index) order, so resuming at the first log a page did not
/// return can neither skip nor repeat a log. <see cref="ToBlock"/> pins the end of the range as resolved by the first
/// page, so a query ending at <c>latest</c> keeps a fixed end while the head moves.
/// </para>
/// <para>
/// Wire format (base64url, no padding): version (1 byte), block, log index and end block (big-endian 64-bit each), the
/// first 16 bytes of the filter hash, the first 16 bytes of <see cref="Block"/>'s hash (so a resumed page can detect a
/// reorganisation), and the first 16 bytes of an HMAC-SHA256 over everything before it. The HMAC key is
/// random per process, so a cursor cannot be forged or edited, and cursors expire when the node restarts.
/// </para>
/// </remarks>
/// <param name="Block">The block the next page starts at.</param>
/// <param name="LogIndex">The smallest block-level log index the next page returns within <paramref name="Block"/>.</param>
/// <param name="ToBlock">The last block of the whole query, inclusive.</param>
/// <param name="FilterHash">Identifies the filter (block selectors, addresses and topics) the cursor belongs to.</param>
/// <param name="BlockHash">The hash of <paramref name="Block"/> when the cursor was issued; only its first <see cref="BlockHashLength"/> bytes are kept.</param>
internal readonly record struct McpLogCursor(ulong Block, ulong LogIndex, ulong ToBlock, ReadOnlyMemory<byte> FilterHash, ReadOnlyMemory<byte> BlockHash)
{
    /// <summary>The length of the filter hash prefix stored in a cursor.</summary>
    public const int FilterHashLength = 16;

    /// <summary>The length of the block hash prefix stored in a cursor.</summary>
    public const int BlockHashLength = 16;

    private const byte Version = 2;
    private const int MacLength = 16;
    private const int PayloadLength = 1 + 3 * sizeof(ulong) + FilterHashLength + BlockHashLength;
    private const int Length = PayloadLength + MacLength;

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    /// <summary>Encodes this cursor as an opaque, authenticated base64url string.</summary>
    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[Length];
        bytes[0] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(bytes[1..], Block);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[9..], LogIndex);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[17..], ToBlock);
        FilterHash.Span[..FilterHashLength].CopyTo(bytes[25..]);
        BlockHash.Span[..BlockHashLength].CopyTo(bytes[(25 + FilterHashLength)..]);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Key, bytes[..PayloadLength], mac);
        mac[..MacLength].CopyTo(bytes[PayloadLength..]);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Decodes and authenticates a cursor produced by <see cref="Encode"/>.</summary>
    public static bool TryDecode(string? text, [NotNullWhen(false)] out string? error, out McpLogCursor cursor)
    {
        cursor = default;
        error = "'cursor' is not a valid get_logs cursor (it is malformed, was edited, or the node restarted since it was issued); repeat the query without 'cursor'.";
        if (text is null || text.Length > 2 * Length)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length + 3];
        if (!Base64Url.IsValid(text) || !Base64Url.TryDecodeFromChars(text, bytes, out int written) || written != Length || bytes[0] != Version)
        {
            return false;
        }

        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Key, bytes[..PayloadLength], mac);
        if (!CryptographicOperations.FixedTimeEquals(mac[..MacLength], bytes[PayloadLength..Length]))
        {
            return false;
        }

        cursor = new McpLogCursor(
            BinaryPrimitives.ReadUInt64BigEndian(bytes[1..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[9..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[17..]),
            bytes[25..(25 + FilterHashLength)].ToArray(),
            bytes[(25 + FilterHashLength)..PayloadLength].ToArray());
        if (cursor.Block > cursor.ToBlock)
        {
            cursor = default;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Returns whether this cursor was issued for the filter identified by <paramref name="filterHash"/>.</summary>
    public bool Matches(ReadOnlySpan<byte> filterHash) =>
        FilterHash.Span.SequenceEqual(filterHash[..FilterHashLength]);

    /// <summary>Returns whether <paramref name="hash"/> is still the hash the cursor saw for <see cref="Block"/>.</summary>
    public bool IsAt(Hash256? hash) => hash is not null && BlockHash.Span.SequenceEqual(hash.Bytes[..BlockHashLength]);

    /// <summary>
    /// Hashes the parts of a <c>get_logs</c> query that must stay the same across pages: the block selectors as given,
    /// the address set and the topic filter. Address order and the order of alternatives at a topic position do not matter.
    /// </summary>
    public static byte[] ComputeFilterHash(BlockParameter fromBlock, BlockParameter toBlock, HashSet<AddressAsKey>? addresses, Hash256[]?[]? topics)
    {
        using MemoryStream stream = new();
        WriteBlock(stream, fromBlock);
        WriteBlock(stream, toBlock);

        List<string> sortedAddresses = [];
        if (addresses is not null)
        {
            foreach (AddressAsKey address in addresses)
            {
                sortedAddresses.Add(((Address)address).ToString());
            }
        }

        sortedAddresses.Sort(StringComparer.Ordinal);
        WriteInt(stream, sortedAddresses.Count);
        foreach (string address in sortedAddresses)
        {
            stream.Write(System.Text.Encoding.ASCII.GetBytes(address));
        }

        WriteInt(stream, topics?.Length ?? 0);
        if (topics is not null)
        {
            foreach (Hash256[]? position in topics)
            {
                if (position is null)
                {
                    WriteInt(stream, -1);
                    continue;
                }

                Hash256[] sorted = (Hash256[])position.Clone();
                Array.Sort(sorted);
                WriteInt(stream, sorted.Length);
                foreach (Hash256 topic in sorted)
                {
                    stream.Write(topic.Bytes);
                }
            }
        }

        return Keccak.Compute(stream.ToArray()).BytesToArray()[..FilterHashLength];
    }

    private static void WriteBlock(MemoryStream stream, BlockParameter block)
    {
        stream.WriteByte((byte)block.Type);
        if (block.BlockNumber is { } number)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(buffer, number);
            stream.Write(buffer);
        }
        else if (block.BlockHash is { } hash)
        {
            stream.Write(hash.Bytes);
        }
    }

    private static void WriteInt(MemoryStream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
