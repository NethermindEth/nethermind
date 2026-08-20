// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Sidecar bloom filter for one immutable base shard table: it answers "definitely not here" without
/// descending into the table, which is the miss path the sorted-table format has no other way to shorten.
/// </summary>
/// <remarks>
/// <para>
/// The filter lives in its own file next to the table rather than inside it, so the shard registry
/// (which records only the table) needs no format change and a filter is always optional: a missing,
/// truncated or otherwise unreadable one degrades to "no filter", never to a wrong answer.
/// </para>
/// <para>
/// That degradation is only safe because a partially written filter is never trusted — a short read
/// would drop bits and turn into a <em>false negative</em>, which would hide live state. The file is
/// therefore fsynced before its table is registered, and <see cref="TryLoad"/> rejects any file whose
/// length disagrees with the header.
/// </para>
/// </remarks>
internal static class BaseTableFilter
{
    internal const string FileExtension = ".bf";

    private static ReadOnlySpan<byte> Magic => "FBF1"u8;
    private const int HeaderLength = 4 + sizeof(long) + sizeof(double) + sizeof(long);

    /// <summary>
    /// Bloom key for a base-table key. The keys are already keccak-derived, but every key in a shard
    /// shares that shard's leading bits (they are what routes it there), so the raw leading bytes carry
    /// no intra-shard entropy — hence a full-key hash rather than a prefix read.
    /// </summary>
    internal static ulong KeyHash(scoped ReadOnlySpan<byte> key) => XxHash3.HashToUInt64(key);

    internal static string FileNameFor(string tableFileName) =>
        Path.ChangeExtension(tableFileName, FileExtension);

    /// <summary>Builds a filter over <paramref name="hashes"/> and writes it durably to
    /// <paramref name="path"/>. Returns the filter, or <c>null</c> when filtering is disabled.</summary>
    internal static BloomFilter.BloomFilter? Write(string path, List<ulong> hashes, double bitsPerKey)
    {
        if (bitsPerKey <= 0 || hashes.Count == 0) return null;

        BloomFilter.BloomFilter filter = new(hashes.Count, bitsPerKey);
        try
        {
            foreach (ulong hash in hashes) filter.Add(hash);

            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Span<byte> header = stackalloc byte[HeaderLength];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteInt64LittleEndian(header[4..], filter.Capacity);
            BinaryPrimitives.WriteDoubleLittleEndian(header[12..], filter.BitsPerKey);
            BinaryPrimitives.WriteInt64LittleEndian(header[20..], filter.Count);
            stream.Write(header);
            stream.Write(filter.RawBits);
            stream.Flush(flushToDisk: true);

            return filter;
        }
        catch
        {
            filter.Dispose();
            throw;
        }
    }

    /// <summary>Restores the filter written for a shard table, or <c>null</c> when there is none to
    /// trust. Never throws for a damaged file — the caller simply reads without a filter.</summary>
    internal static BloomFilter.BloomFilter? TryLoad(string path, ILogger logger)
    {
        if (!File.Exists(path)) return null;

        BloomFilter.BloomFilter? filter = null;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[HeaderLength];
            if (stream.Length < HeaderLength) return Reject(path, logger, "shorter than its header");
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual(Magic)) return Reject(path, logger, "has an unrecognized signature");

            long capacity = BinaryPrimitives.ReadInt64LittleEndian(header[4..]);
            double bitsPerKey = BinaryPrimitives.ReadDoubleLittleEndian(header[12..]);
            long count = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
            if (capacity <= 0 || bitsPerKey <= 0 || double.IsNaN(bitsPerKey) || double.IsInfinity(bitsPerKey))
                return Reject(path, logger, "declares an out-of-range capacity or bits-per-key");

            filter = new BloomFilter.BloomFilter(capacity, bitsPerKey, count);
            if (stream.Length - HeaderLength != filter.DataBytes)
                return Reject(path, logger, "length disagrees with its header", filter);

            stream.ReadExactly(filter.RawBits);
            return filter;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            filter?.Dispose();
            if (logger.IsWarn) logger.Warn($"Could not read flat base filter '{Path.GetFileName(path)}': {e.Message}. Reading without it.");
            return null;
        }
    }

    private static BloomFilter.BloomFilter? Reject(string path, ILogger logger, string reason, BloomFilter.BloomFilter? filter = null)
    {
        filter?.Dispose();
        if (logger.IsWarn) logger.Warn($"Ignoring flat base filter '{Path.GetFileName(path)}': it {reason}. Reading without it.");
        return null;
    }
}
