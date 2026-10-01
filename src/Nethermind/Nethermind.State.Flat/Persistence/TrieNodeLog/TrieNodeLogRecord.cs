// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.Hashing;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// Header of one log record. Layout:
/// <c>u8 type | u8 keyLength | u16 valueLength | u64 version | u64 prev | key | value</c>. The column is not stored:
/// every shard holds records of one column.
/// </summary>
/// <remarks>
/// <see cref="Prev"/> is the <see cref="PackLocation">packed location</see> of the previous record of the same key
/// (0 when none), which lets a reader walk back to the version it needs. A commit record has no key or value and
/// stores <c>~version</c> in <see cref="Prev"/> so a torn (zero-filled) tail cannot pass as a commit.
/// </remarks>
internal readonly record struct TrieNodeLogRecord(byte Type, int KeyLength, int ValueLength, ulong Version, ulong Prev)
{
    public const byte Put = 0;
    public const byte Delete = 1;
    public const byte Commit = 2;

    public const int HeaderLength = 1 + 1 + 2 + 8 + 8;
    public const int MaxKeyLength = byte.MaxValue;
    public const int MaxValueLength = ushort.MaxValue;

    // Packed location: generation in the high 24 bits, (offset + 1) in the low 40 bits so that 0 means "none".
    private const int OffsetBits = 40;
    private const ulong OffsetMask = (1UL << OffsetBits) - 1;

    public int Length => HeaderLength + KeyLength + ValueLength;

    public bool IsCommit => Type == Commit;

    public bool IsValidCommit => Type == Commit && KeyLength == 0 && ValueLength == 0 && Prev == ~Version;

    public static TrieNodeLogRecord CommitRecord(ulong version) => new(Commit, 0, 0, version, ~version);

    public void Write(Span<byte> destination)
    {
        destination[0] = Type;
        destination[1] = (byte)KeyLength;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)ValueLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], Prev);
    }

    public static TrieNodeLogRecord Read(ReadOnlySpan<byte> source) => new(
        source[0],
        source[1],
        BinaryPrimitives.ReadUInt16LittleEndian(source[2..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[4..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[12..]));

    /// <summary>Whether the header can be a record written by this log (a zero-filled tail fails this).</summary>
    public bool IsPlausible => Type switch
    {
        Put => KeyLength > 0 && ValueLength >= 0,
        Delete => KeyLength > 0 && ValueLength == 0,
        Commit => IsValidCommit,
        _ => false,
    };

    public static ulong Hash(ReadOnlySpan<byte> key) => XxHash3.HashToUInt64(key);

    public static ulong PackLocation(ulong generation, long offset) => (generation << OffsetBits) | ((ulong)offset + 1);

    public static ulong LocationGeneration(ulong location) => location >> OffsetBits;

    public static long LocationOffset(ulong location) => (long)(location & OffsetMask) - 1;

    /// <summary>Index slot: the hash's high 24 bits as a tag over the packed (offset + 1); 0 is an empty slot.</summary>
    public static ulong PackSlot(ulong hash, long offset) => (hash & ~OffsetMask) | ((ulong)offset + 1);

    public static bool SlotTagMatches(ulong slot, ulong hash) => (slot & ~OffsetMask) == (hash & ~OffsetMask);

    public static long SlotOffset(ulong slot) => (long)(slot & OffsetMask) - 1;
}
