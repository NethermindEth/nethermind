// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Encodes and reads a four-level node group's canonical node payload.</summary>
/// <remarks>
/// The physical payload starts with version byte 7, followed by entries and a variable-size footer.
/// Entries are complete branch encodings in ascending post-order position order, with no padding or
/// separators; leaves are inlined in their parent branch's trailer past the whole bytes of the group
/// path (see <see cref="PbtNodeCodec"/>), so the only leaf entry is the root of a single-leaf tree. The footer contains one little-endian
/// unsigned 16-bit offset per physically stored node, in the same order, followed by a little-endian
/// unsigned 32-bit availability bitmap, one little-endian descendant size per set bit of the closing
/// little-endian unsigned 16-bit descendant mask, in ascending boundary-slot order, the width byte of
/// those sizes when the mask is nonzero, and that mask. Every size takes the width, 1–6 bytes, of the
/// largest one, since the subtrees below a group are of similar size. Bit <c>n</c> of the mask is set exactly when the groups physically stored below boundary
/// slot <c>n</c> have a nonzero summed payload length, which is the size recorded for the slot. Keeping
/// the sizes per slot lets a group created between existing groups take its descendants' size from its
/// parent instead of reading them. Offsets are relative to the beginning of the entries
/// section. The first offset is zero, and subsequent offsets strictly increase; a node ends at the next
/// offset or the footer's beginning. Position 30 is reserved for the root and may only be present in
/// the depth-zero root group. The group key is deliberately kept outside this payload. Prefixless
/// branches at relative depths 1–3 are omitted when neither child is an inline leaf: only their
/// descendants are stored. Availability describes physical entries.
/// </remarks>
public static class PbtNodeGroupCodec
{
    internal const int HeaderLength = 1;
    internal static ReadOnlySpan<byte> Header => "\x07"u8;

    /// <summary>The number of bytes in the widest descendant-size field.</summary>
    public const int MaxDescendantBytesLength = 6;

    private const int DescendantWidthLength = 1;

    /// <summary>The number of bytes in the descendant mask that ends the footer.</summary>
    public const int DescendantMaskLength = sizeof(ushort);

    /// <summary>The maximum number of bytes in the packed offset, availability and descendant-size footer.</summary>
    public const int MaxTrailerLength = PbtFourLevelGroupGeometry.PositionCount * sizeof(ushort) + sizeof(uint) + PbtFourLevelGroupGeometry.BoundarySlots * MaxDescendantBytesLength + DescendantWidthLength + DescendantMaskLength;

    /// <summary>The largest descendant size a 48-bit field can hold.</summary>
    public const long MaxDescendantBytes = (1L << (8 * MaxDescendantBytesLength)) - 1;

    /// <summary>The largest payload a group can encode: header, a full entries section, and the widest trailer.</summary>
    public const int MaxPayloadLength = HeaderLength + MaxEntriesLength + MaxTrailerLength;
    private const uint ReservedRootBit = 1u << PbtFourLevelGroupGeometry.RootPosition;
    private const uint AllowedPositionBits = (1u << PbtFourLevelGroupGeometry.PositionCount) - 1;

    /// <summary>Checks a payload's header, footer length, availability bits and descendant sizes without parsing its nodes.</summary>
    /// <returns>The availability bitmap.</returns>
    internal static uint ValidateFraming(int groupDepth, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength || !payload[..HeaderLength].SequenceEqual(Header))
            throw new InvalidDataException("Unsupported or missing PBT node group format header.");
        if (payload.Length < HeaderLength + sizeof(uint) + DescendantMaskLength) throw new InvalidDataException("Truncated PBT node group footer.");
        ushort descendantMask = ReadDescendantMask(payload);
        if (descendantMask != 0 && ReadDescendantWidth(payload) is < 1 or > MaxDescendantBytesLength) throw new InvalidDataException("Invalid PBT node group descendant-size width.");
        if (payload.Length < HeaderLength + sizeof(uint) + DescendantsLength(payload)) throw new InvalidDataException("Truncated PBT node group footer.");
        long widest = 0;
        for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
        {
            if ((descendantMask & (1 << slot)) == 0) continue;
            long slotBytes = ReadDescendantBytes(payload, descendantMask, slot);
            if (slotBytes == 0) throw new InvalidDataException("PBT node group descendant mask marks an empty slot.");
            widest = Math.Max(widest, slotBytes);
        }
        if (ByteWidth(widest) != ReadDescendantWidth(payload)) throw new InvalidDataException("PBT node group descendant sizes are wider than the largest one.");
        payload = payload[HeaderLength..];
        uint availability = ReadAvailability(payload);
        if (availability == 0 || (availability & ~AllowedPositionBits) != 0 || (groupDepth != 0 && (availability & ReservedRootBit) != 0))
            throw new InvalidDataException("Invalid PBT node group availability bits.");
        if (payload.Length < GetTrailerLength(availability, payload)) throw new InvalidDataException("Truncated PBT node group offset table.");
        return availability;
    }

    /// <summary>Checks every node of a payload on top of <see cref="ValidateFraming"/>: the offset table delimits positive-length nodes and each node is structurally exact.</summary>
    /// <remarks>
    /// Runs in every build, for callers whose job is to vet stored data, such as the scanner; readers trust their
    /// payloads and only check them in debug builds, through <see cref="DebugValidateNodes{TPath}"/>.
    /// </remarks>
    internal static void ValidateNodes(scoped in PbtTraversalPath path, ReadOnlySpan<byte> payload)
    {
        uint availability = ValidateFraming(path.BitDepth, payload);
        payload = payload[HeaderLength..];
        int entriesLength = payload.Length - GetTrailerLength(availability, payload);
        if (entriesLength > MaxEntriesLength) throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
        ReadOnlySpan<byte> footer = payload[entriesLength..];

        int start = BinaryPrimitives.ReadUInt16LittleEndian(footer);
        if (start != 0) throw new InvalidDataException("The first PBT node offset must be zero.");
        int offsetsLength = BitOperations.PopCount(availability) * sizeof(ushort);
        int offsetIndex = sizeof(ushort);
        for (uint remaining = availability; remaining != 0; remaining &= remaining - 1, offsetIndex += sizeof(ushort))
        {
            int end = offsetIndex < offsetsLength ? BinaryPrimitives.ReadUInt16LittleEndian(footer[offsetIndex..]) : entriesLength;
            if (end <= start) throw new InvalidDataException("PBT node offsets must strictly increase.");
            if (end > entriesLength) throw new InvalidDataException("PBT node offset is outside the entries section.");
            ReadOnlySpan<byte> encoding = payload[start..end];
            try
            {
                ValidateNode(path, BitOperations.TrailingZeroCount(remaining), encoding);
            }
            catch (InvalidDataException exception) { throw new InvalidDataException("Invalid PBT node in group.", exception); }
            start = end;
        }
    }

    /// <inheritdoc cref="ValidateNodes(in PbtTraversalPath, ReadOnlySpan{byte})"/>
    internal static void ValidateNodes<TPath>(TPath groupKey, ReadOnlySpan<byte> payload) where TPath : struct, IPbtNodePath<TPath> =>
        ValidateNodes(PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], groupKey), payload);

    /// <summary>Debug-build guard for a payload entering or leaving a store; readers on the update path trust stored payloads.</summary>
    [Conditional("DEBUG")]
    internal static void DebugValidateNodes<TPath>(TPath groupKey, ReadOnlySpan<byte> payload) where TPath : struct, IPbtNodePath<TPath> =>
        ValidateNodes(groupKey, payload);

    /// <summary>Reads the descendant mask that ends a payload, without validating anything else.</summary>
    public static ushort ReadDescendantMask(ReadOnlySpan<byte> payload) => BinaryPrimitives.ReadUInt16LittleEndian(payload[^DescendantMaskLength..]);

    /// <summary>Reads the availability bitmap of a payload, with or without its header, without validating anything else.</summary>
    internal static uint ReadAvailability(ReadOnlySpan<byte> payload) =>
        BinaryPrimitives.ReadUInt32LittleEndian(payload[^(sizeof(uint) + DescendantsLength(payload))..]);

    /// <summary>Reads the subtree size of every boundary slot that has one into <paramref name="descendantBytes"/>, leaving the other slots untouched.</summary>
    internal static void ReadDescendantBytes(ReadOnlySpan<byte> payload, Span<long> descendantBytes)
    {
        ushort mask = ReadDescendantMask(payload);
        for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
            if ((mask & (1 << slot)) != 0) descendantBytes[slot] = ReadDescendantBytes(payload, mask, slot);
    }

    internal static long ReadDescendantBytes(ReadOnlySpan<byte> payload, ushort descendantMask, int slot)
    {
        int width = ReadDescendantWidth(payload);
        int fieldsAfter = BitOperations.PopCount((uint)(descendantMask >> (slot + 1)));
        ReadOnlySpan<byte> field = payload[^(DescendantWidthLength + DescendantMaskLength + (fieldsAfter + 1) * width)..][..width];
        long slotBytes = 0;
        for (int index = width - 1; index >= 0; index--) slotBytes = slotBytes << 8 | field[index];
        return slotBytes;
    }

    private static int ReadDescendantWidth(ReadOnlySpan<byte> payload) =>
        ReadDescendantMask(payload) == 0 ? 0 : payload[^(DescendantWidthLength + DescendantMaskLength)];

    private static int DescendantsLength(ushort descendantMask, int width) =>
        descendantMask == 0 ? DescendantMaskLength : BitOperations.PopCount(descendantMask) * width + DescendantWidthLength + DescendantMaskLength;

    private static int DescendantsLength(ReadOnlySpan<byte> payload) => DescendantsLength(ReadDescendantMask(payload), ReadDescendantWidth(payload));

    /// <summary>The width every descendant-size field takes: the byte count of the largest masked size, or zero for none.</summary>
    private static int DescendantWidth(ReadOnlySpan<long> descendantBytes, ushort descendantMask)
    {
        long widest = 0;
        for (uint remaining = descendantMask; remaining != 0; remaining &= remaining - 1)
            widest = Math.Max(widest, descendantBytes[BitOperations.TrailingZeroCount(remaining)]);
        return ByteWidth(widest);
    }

    private static int ByteWidth(long value) => (64 - BitOperations.LeadingZeroCount((ulong)value) + 7) / 8;

    private static int OffsetsAndAvailabilityLength(uint availability) => BitOperations.PopCount(availability) * sizeof(ushort) + sizeof(uint);

    /// <summary>The footer length of a group being written.</summary>
    /// <param name="descendantMask">The <see cref="DescendantMask(ReadOnlySpan{long}, ushort)"/> of <paramref name="descendantBytes"/>.</param>
    internal static int GetTrailerLength(uint availability, ushort descendantMask, ReadOnlySpan<long> descendantBytes) =>
        OffsetsAndAvailabilityLength(availability) + DescendantsLength(descendantMask, DescendantWidth(descendantBytes, descendantMask));

    /// <summary>The footer length of a stored payload, with or without its header.</summary>
    internal static int GetTrailerLength(uint availability, ReadOnlySpan<byte> payload) =>
        OffsetsAndAvailabilityLength(availability) + DescendantsLength(payload);

    /// <summary>Validates per-slot descendant sizes and returns the mask of nonzero slots; an empty span means no descendants.</summary>
    /// <param name="candidateSlots">The slots that may be nonzero; every other slot is known to be zero and is not read.</param>
    internal static ushort DescendantMask(ReadOnlySpan<long> descendantBytes, ushort candidateSlots)
    {
        if (descendantBytes.IsEmpty) return 0;
        ushort descendantMask = 0;
        for (uint remaining = candidateSlots; remaining != 0; remaining &= remaining - 1)
        {
            int slot = BitOperations.TrailingZeroCount(remaining);
            long slotBytes = descendantBytes[slot];
            ArgumentOutOfRangeException.ThrowIfNegative(slotBytes, nameof(descendantBytes));
            if (slotBytes > MaxDescendantBytes) throw new InvalidDataException("PBT node group descendant size exceeds the uint48 limit.");
            if (slotBytes != 0) descendantMask |= (ushort)(1 << slot);
        }
        return descendantMask;
    }

    /// <param name="descendantMask">The <see cref="DescendantMask(ReadOnlySpan{long}, ushort)"/> of <paramref name="descendantBytes"/>.</param>
    internal static void WriteFooter(Span<byte> footer, ReadOnlySpan<ushort> offsets, uint availability, ushort descendantMask, ReadOnlySpan<long> descendantBytes)
    {
        int offsetIndex = 0;
        for (uint remaining = availability; remaining != 0; remaining &= remaining - 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(footer[offsetIndex..], offsets[BitOperations.TrailingZeroCount(remaining)]);
            offsetIndex += sizeof(ushort);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(footer[offsetIndex..], availability);
        Span<byte> field = footer[(offsetIndex + sizeof(uint))..];
        int width = DescendantWidth(descendantBytes, descendantMask);
        for (uint remaining = descendantMask; remaining != 0; remaining &= remaining - 1)
        {
            long slotBytes = descendantBytes[BitOperations.TrailingZeroCount(remaining)];
            for (int index = 0; index < width; index++) field[index] = (byte)(slotBytes >> (8 * index));
            field = field[width..];
        }
        if (descendantMask != 0)
        {
            field[0] = (byte)width;
            field = field[DescendantWidthLength..];
        }
        BinaryPrimitives.WriteUInt16LittleEndian(field, descendantMask);
    }

    private static readonly int PrefixlessBranchLength = PbtNodeCodec.BranchLength(0, 0, 0);

    /// <summary>A prefixless interior branch without inline leaves is reconstructed from its children, so it need not be stored.</summary>
    /// <remarks>Relative depth 1 is width 8, depth 2 width 4 and depth 3 width 2 in <see cref="PbtFourLevelGroupGeometry.WidthOf"/>.</remarks>
    internal static bool ShouldOmit(int position, ReadOnlySpan<byte> encoding) =>
        PbtFourLevelGroupGeometry.WidthOf(position) is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots
        && encoding.Length == PrefixlessBranchLength && encoding[0] == 1 && encoding[1] == 0 && encoding[2] == 0
        && encoding[PrefixlessBranchLength - 2] == 0 && encoding[PrefixlessBranchLength - 1] == 0;

    /// <summary>The most bytes a group's entries may take together, since they are addressed by little-endian uint16 offsets.</summary>
    internal const int MaxEntriesLength = ushort.MaxValue;

    /// <summary>Debug-build check of one node at its group position: an exact encoding, and inline leaves that lie below the branch.</summary>
    /// <remarks>Release readers trust stored encodings; the group framing is still checked by <see cref="ValidateFraming"/>.</remarks>
    [Conditional("DEBUG")]
    internal static void DebugValidateNode(scoped in PbtTraversalPath path, int position, ReadOnlySpan<byte> encoding) =>
        ValidateNode(path, position, encoding);

    /// <summary>Checks that a node's encoding is exact and that a branch's inline leaf keys lie below the branch's position.</summary>
    private static void ValidateNode(scoped in PbtTraversalPath groupKey, int position, ReadOnlySpan<byte> encoding)
    {
        PbtNodeCodec.ThrowIfNotExact(encoding);
        if (encoding[0] == 0)
        {
            if (position != PbtFourLevelGroupGeometry.RootPosition) throw new InvalidDataException("A PBT leaf entry is only valid as the tree root.");
            return;
        }
        if (position == PbtFourLevelGroupGeometry.RootPosition) return;
        PbtBranchReader node = PbtBranchReader.FromValidated(encoding);
        NodeGroupPath local = PbtFourLevelGroupGeometry.LocalPathOf(position);
        ValidateInlineLeafPath(groupKey, node.LeftKeyPostfix, local);
        ValidateInlineLeafPath(groupKey, node.RightKeyPostfix, local);
    }

    private static void ValidateInlineLeafPath(scoped in PbtTraversalPath groupKey, ReadOnlySpan<byte> keyPostfix, NodeGroupPath local)
    {
        if (keyPostfix.IsEmpty) return;
        int relativeDepth = local.Length;
        // The leaf hangs at least one level below the branch.
        int completeBytes = groupKey.BitDepth >> 3;
        int requiredDepth = checked(groupKey.BitDepth + relativeDepth + 1);
        if ((completeBytes + keyPostfix.Length) * 8 < requiredDepth) throw new InvalidDataException("PBT leaf does not match its group position.");
        if (completeBytes + keyPostfix.Length > PbtVariableTreeKey.MaxLength) throw new InvalidDataException("An inline PBT leaf key exceeds the maximum key length.");

        // Four-level group alignment keeps the group tail and relative path in the postfix's first byte.
        int groupTailBits = groupKey.BitDepth & 7;
        int expectedTail = groupTailBits == 0 ? 0 : groupKey.Bytes[completeBytes];
        expectedTail |= local.Slot << (4 - groupTailBits);
        int tailMask = 0xFF << (8 - groupTailBits - relativeDepth);
        if (((keyPostfix[0] ^ expectedTail) & tailMask) != 0)
            throw new InvalidDataException("PBT leaf does not match its group position.");
    }
}
