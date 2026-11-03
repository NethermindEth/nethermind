// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Paprika;
using Paprika.Data;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>
/// The markers of <see cref="Db.FlatLayout.PaprikaFlat"/> as of one Paprika root: the state pointer, the wipe marker, the
/// format markers and the trie node log's confirmed versions. An instance never changes.
/// </summary>
/// <remarks>
/// <para>
/// The keys are those of the RocksDB metadata column, so <see cref="BasePersistence"/>'s marker helpers and the trie node
/// log read them here, and write them through a <see cref="ChangeSet"/>, unchanged.
/// </para>
/// <para>
/// They are stored as one blob at <see cref="BlobKey"/>, so they commit atomically with the flat data. The key is shorter
/// than two nibbles, so it lives in the state root page's own map, which neither iterating the account keys nor deleting
/// slots reaches; no state prefix delete shorter than two nibbles is ever made, as one could reach it. The blob is
/// <c>format:u8 | count:u8 | (keyLength:u8 key valueLength:u8 value)*</c>, keys sorted and unique, at most
/// <see cref="MaxBlobSize"/> bytes.
/// </para>
/// </remarks>
internal sealed class PaprikaFlatMetadata : IReadOnlyKeyValueStore
{
    /// <summary>The format of the blob, its first byte.</summary>
    public const byte FormatVersion = 1;

    /// <summary>The most bytes the blob may take in the state root page's map.</summary>
    public const int MaxBlobSize = 2048;

    private const int HeaderSize = 2;

    /// <summary>No markers, as in a fresh Paprika file.</summary>
    public static PaprikaFlatMetadata Empty { get; } = new([]);

    private readonly KeyValuePair<byte[], byte[]>[] _entries;

    private PaprikaFlatMetadata(KeyValuePair<byte[], byte[]>[] entries) => _entries = entries;

    /// <summary>The Paprika key of the blob.</summary>
    public static Key BlobKey => Key.Account(NibblePath.Empty);

    /// <summary>Reads the markers of the root <paramref name="batch"/> reads.</summary>
    /// <exception cref="InvalidDataException">The blob is corrupt.</exception>
    public static PaprikaFlatMetadata Read(IReadOnlyBatch batch) =>
        batch.TryGet(BlobKey, out ReadOnlySpan<byte> blob) ? Decode(blob) : Empty;

    /// <summary>Decodes a blob; an empty one holds no markers.</summary>
    /// <exception cref="InvalidDataException">The blob is corrupt.</exception>
    public static PaprikaFlatMetadata Decode(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty) return Empty;
        if (blob.Length is < HeaderSize or > MaxBlobSize || blob[0] != FormatVersion) ThrowCorrupt();

        KeyValuePair<byte[], byte[]>[] entries = new KeyValuePair<byte[], byte[]>[blob[1]];
        ReadOnlySpan<byte> rest = blob[HeaderSize..];
        for (int i = 0; i < entries.Length; i++)
        {
            byte[] key = ReadField(ref rest);
            if (key.Length == 0 || (i > 0 && entries[i - 1].Key.AsSpan().SequenceCompareTo(key) >= 0)) ThrowCorrupt();
            entries[i] = new(key, ReadField(ref rest));
        }

        if (!rest.IsEmpty) ThrowCorrupt();
        return new(entries);
    }

    /// <summary>Encodes the blob.</summary>
    public byte[] Encode()
    {
        byte[] blob = new byte[BlobSize(_entries)];
        blob[0] = FormatVersion;
        blob[1] = (byte)_entries.Length;
        Span<byte> rest = blob.AsSpan(HeaderSize);
        foreach ((byte[] key, byte[] value) in _entries)
        {
            rest = WriteField(rest, key);
            rest = WriteField(rest, value);
        }

        return blob;
    }

    /// <summary>Writes the blob to <paramref name="batch"/>.</summary>
    public void WriteTo(IBatch batch) => batch.SetRaw(BlobKey, Encode());

    /// <summary>Returns these markers with <paramref name="changes"/> applied.</summary>
    /// <param name="changes">The markers to put, and to remove where the value is null.</param>
    /// <exception cref="ArgumentException">A key is empty or longer than 255 bytes, or a value is longer than 255 bytes.</exception>
    /// <exception cref="InvalidOperationException">The blob would hold more than 255 markers or take more than <see cref="MaxBlobSize"/> bytes.</exception>
    public PaprikaFlatMetadata Merge(IReadOnlyDictionary<byte[], byte[]?> changes)
    {
        if (changes.Count == 0) return this;

        SortedDictionary<byte[], byte[]> merged = new(Bytes.Comparer);
        foreach ((byte[] key, byte[] value) in _entries) merged[key] = value;
        foreach ((byte[] key, byte[]? value) in changes)
        {
            if (value is null) merged.Remove(key);
            else merged[key] = value;
        }

        KeyValuePair<byte[], byte[]>[] entries = [.. merged];
        int size = BlobSize(entries);
        if (entries.Length > byte.MaxValue || size > MaxBlobSize)
        {
            throw new InvalidOperationException(
                $"The PaprikaFlat metadata would hold {entries.Length} markers in {size} bytes; at most {byte.MaxValue} markers in {MaxBlobSize} bytes fit.");
        }

        return new(entries);
    }

    /// <inheritdoc/>
    /// <remarks>Returns a copy, so the markers stay unchanged for every reader that shares them.</remarks>
    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        foreach ((byte[] entryKey, byte[] value) in _entries)
        {
            if (key.SequenceEqual(entryKey)) return value.ToArray();
        }

        return null;
    }

    private static int BlobSize(KeyValuePair<byte[], byte[]>[] entries)
    {
        int size = HeaderSize;
        foreach ((byte[] key, byte[] value) in entries)
        {
            if (key.Length is 0 or > byte.MaxValue || value.Length > byte.MaxValue)
            {
                throw new ArgumentException($"A PaprikaFlat marker key is 1 to {byte.MaxValue} bytes and its value at most {byte.MaxValue} bytes; got {key.Length} and {value.Length}.");
            }

            size += 2 + key.Length + value.Length;
        }

        return size;
    }

    private static byte[] ReadField(ref ReadOnlySpan<byte> rest)
    {
        if (rest.IsEmpty || rest.Length <= rest[0]) ThrowCorrupt();
        byte[] field = rest.Slice(1, rest[0]).ToArray();
        rest = rest[(1 + field.Length)..];
        return field;
    }

    private static Span<byte> WriteField(Span<byte> destination, byte[] field)
    {
        destination[0] = (byte)field.Length;
        field.CopyTo(destination[1..]);
        return destination[(1 + field.Length)..];
    }

    [DoesNotReturn]
    private static void ThrowCorrupt() => throw new InvalidDataException("The PaprikaFlat metadata blob is corrupt.");

    /// <summary>Collects marker puts and removes, for a later <see cref="Merge"/>.</summary>
    /// <remarks>Used by one thread at a time; a later write of a key replaces an earlier one.</remarks>
    public sealed class ChangeSet : IWriteOnlyKeyValueStore
    {
        private readonly Dictionary<byte[], byte[]?> _changes = new(Bytes.EqualityComparer);

        /// <summary>The markers to put, and to remove where the value is null.</summary>
        public IReadOnlyDictionary<byte[], byte[]?> Changes => _changes;

        /// <inheritdoc/>
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => _changes[key.ToArray()] = value;
    }
}
