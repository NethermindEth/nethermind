// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Pbt.NodeBuffer;

public enum PbtGroupRecordKind : byte
{
    /// <summary>A node group with its descendant links.</summary>
    Group,

    /// <summary>A buffer-only record indexing a tree root; never a group.</summary>
    RootKey,
}

/// <summary>The byte layout of a group record, as the group record buffer stores it.</summary>
/// <remarks>
/// A group record is a <see cref="PbtGroupRecordHeader"/> followed by the group's canonical payload. A root key record is
/// <see cref="RootKeyLength"/> bytes long instead. Both start with their <see cref="PbtGroupRecordKind"/>, so a zero-filled
/// header reads as a group record without a payload, which no write produces.
/// </remarks>
public static class PbtGroupRecordCodec
{
    public const int OffsetBits = 32;
    public const int RootKeyLength = 56;
    public const int RecordAlignment = 8;

    public static readonly int HeaderLength = Unsafe.SizeOf<PbtGroupRecordHeader>();

    /// <summary>Length of the record that starts with <paramref name="header"/>.</summary>
    public static int Length(ReadOnlySpan<byte> header) => (PbtGroupRecordKind)header[0] == PbtGroupRecordKind.RootKey
        ? RootKeyLength
        : Length(MemoryMarshal.AsRef<PbtGroupRecordHeader>(header).PayloadLength);

    public static int Length(int payloadLength) => HeaderLength + payloadLength;

    public static int AlignedLength(int length) => (length + RecordAlignment - 1) & -RecordAlignment;

    /// <summary>Whether <paramref name="group"/> is a group record rather than a group's payload.</summary>
    /// <remarks>A group record starts with its <see cref="PbtGroupRecordKind.Group"/> kind, while a payload starts with <see cref="PbtNodeGroupCodec.Header"/>.</remarks>
    public static bool IsRecord(ReadOnlySpan<byte> group) => group[0] == (byte)PbtGroupRecordKind.Group;

    /// <summary>A well-mixed 64-bit hash of a group path, which identifies the records of one group position.</summary>
    public static ulong PathKeyHash(in PbtStorageNodePath path)
    {
        ulong key = (ulong)path.BitDepth * 0xC2B2AE3D27D4EB4F;
        int byteCount = PbtBitPrefix.ByteCount(path.BitDepth);
        for (int index = 0; index < byteCount; index++)
            key = BitOperations.RotateLeft(key ^ path.GetByte(index), 8) * 0x9E3779B97F4A7C15;

        // The MurmurHash3 64-bit finalizer.
        key ^= key >> 33;
        key *= 0xff51afd7ed558ccd;
        key ^= key >> 33;
        key *= 0xc4ceb9fe1a85ec53;
        return key ^ key >> 33;
    }

    /// <summary>Writes the header of a group record into the zero-filled <paramref name="record"/>.</summary>
    /// <param name="record">At least <see cref="Length(int)"/> bytes.</param>
    /// <returns>The record's payload, for the caller to fill.</returns>
    public static Span<byte> WriteGroup(Span<byte> record, in PbtStorageNodePath path, in PbtGroupLinks links, int payloadLength)
    {
        MemoryMarshal.Write(record, new PbtGroupRecordHeader { Kind = PbtGroupRecordKind.Group, PayloadLength = payloadLength, PathKeyHash = PathKeyHash(path), Path = path, Links = links });
        return record.Slice(HeaderLength, payloadLength);
    }
}

/// <summary>The header of a group record.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PbtGroupRecordHeader
{
    public PbtGroupRecordKind Kind;
    public int PayloadLength;
    /// <summary>The <see cref="PbtGroupRecordCodec.PathKeyHash"/> of <see cref="Path"/>.</summary>
    public ulong PathKeyHash;
    public PbtStorageNodePath Path;
    public PbtGroupLinks Links;
}
