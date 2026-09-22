// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>Streams the canonical EIP-8347 leaf artifact without trusting its claimed root.</summary>
/// <remarks>Streams remain caller-owned. Input must be consumed to completion to validate count and EOF.
/// Writers require strictly ordered leaves; they do not sort or retain the input. Cancellation is checked per record.</remarks>
internal static class PbtSnapshotCodec
{
    public static (ValueHash256 Root, ulong Count) ReadHeader(Stream source)
    {
        Span<byte> header = stackalloc byte[40];
        ReadRecord(source, header);
        return (new ValueHash256(header[..32]), BinaryPrimitives.ReadUInt64BigEndian(header[32..]));
    }

    public static IEnumerable<RebuildEntry> ReadLeaves(Stream source, ulong count, CancellationToken cancellationToken = default)
    {
        PbtStorageTreeKey previous = default;
        byte[] buffer = new byte[102];
        for (ulong index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int prefix = source.ReadByte();
            int length = prefix switch
            {
                >= 0xC0 and <= 0xF7 => prefix - 0xC0,
                0xF8 => source.ReadByte(),
                _ => throw new InvalidDataException("Invalid snapshot leaf list prefix.")
            };
            if (length < 0 || length > buffer.Length || (prefix == 0xF8 && length < 56))
                throw new InvalidDataException("Invalid snapshot leaf list length.");
            ReadRecord(source, buffer.AsSpan(0, length));
            RebuildEntry entry = Decode(buffer.AsSpan(0, length));
            Validate(entry, previous);
            previous = entry.Key;
            yield return entry;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (source.ReadByte() != -1) throw new InvalidDataException("Trailing snapshot bytes.");
    }

    public static void Write(Stream destination, in ValueHash256 root, ulong count, IEnumerable<RebuildEntry> leaves, CancellationToken cancellationToken = default)
    {
        Span<byte> header = stackalloc byte[40];
        root.Bytes.CopyTo(header);
        BinaryPrimitives.WriteUInt64BigEndian(header[32..], count);
        destination.Write(header);
        Span<byte> record = stackalloc byte[104];
        PbtStorageTreeKey previous = default;
        ulong written = 0;
        foreach (RebuildEntry entry in leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (written == count) throw new InvalidDataException("Snapshot leaf count exceeded.");
            Validate(entry, previous);
            previous = entry.Key;
            ReadOnlySpan<byte> value = entry.Leaf.Bytes;
            int leadingZeros = 0;
            while (value[leadingZeros] == 0) leadingZeros++;
            value = value[leadingZeros..];
            int keyPrefixLength = entry.Key.Length < 56 ? 1 : 2;
            int valuePrefixLength = value.Length == 1 && value[0] < 0x80 ? 0 : 1;
            int payloadLength = keyPrefixLength + entry.Key.Length + valuePrefixLength + value.Length;
            int position = 0;
            if (payloadLength < 56) record[position++] = (byte)(0xC0 + payloadLength);
            else { record[position++] = 0xF8; record[position++] = (byte)payloadLength; }
            if (keyPrefixLength == 1) record[position++] = (byte)(0x80 + entry.Key.Length);
            else { record[position++] = 0xB8; record[position++] = (byte)entry.Key.Length; }
            entry.Key.Bytes.CopyTo(record[position..]);
            position += entry.Key.Length;
            if (valuePrefixLength != 0) record[position++] = (byte)(0x80 + value.Length);
            value.CopyTo(record[position..]);
            destination.Write(record[..(position + value.Length)]);
            written++;
        }
        if (written != count) throw new InvalidDataException("Snapshot leaf count mismatch.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Fills <paramref name="buffer"/> from the artifact, reporting a stream that ends mid-record as malformed input.</summary>
    internal static void ReadRecord(Stream source, Span<byte> buffer)
    {
        if (source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
            throw new InvalidDataException("Truncated artifact record.");
    }

    private static RebuildEntry Decode(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> key = ReadString(ref payload);
        ReadOnlySpan<byte> value = ReadString(ref payload);
        if (!payload.IsEmpty || value.Length is 0 or > 32 || value[0] == 0)
            throw new InvalidDataException("Invalid snapshot leaf value.");
        ValidateKey(key);
        Span<byte> padded = stackalloc byte[32];
        padded.Clear();
        value.CopyTo(padded[(32 - value.Length)..]);
        return new(new PbtStorageTreeKey(key), new ValueHash256(padded));
    }

    private static ReadOnlySpan<byte> ReadString(ref ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) throw new InvalidDataException("Missing leaf field.");
        int prefix = payload[0];
        int offset = 1;
        int length;
        if (prefix < 0x80) { offset = 0; length = 1; }
        else if (prefix <= 0xB7) length = prefix - 0x80;
        else if (prefix == 0xB8 && payload.Length >= 2 && payload[1] >= 56) { offset = 2; length = payload[1]; }
        else throw new InvalidDataException("Noncanonical leaf field.");
        if (length > payload.Length - offset) throw new InvalidDataException("Truncated leaf field.");
        ReadOnlySpan<byte> result = payload.Slice(offset, length);
        if (offset != 0 && length == 1 && result[0] < 0x80) throw new InvalidDataException("Noncanonical single-byte field.");
        payload = payload[(offset + length)..];
        return result;
    }

    private static void Validate(RebuildEntry entry, in PbtStorageTreeKey previous)
    {
        ValidateKey(entry.Key.Bytes);
        if (entry.Leaf == default || (previous.Length != 0 && previous.CompareTo(entry.Key) >= 0))
            throw new InvalidDataException("Snapshot leaves must be nonzero and strictly ordered.");
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty || key.Length != (key[0] switch { 0 or 1 => 34, 255 => 66, _ => -1 }))
            throw new InvalidDataException("Invalid snapshot key zone or length.");
    }
}
