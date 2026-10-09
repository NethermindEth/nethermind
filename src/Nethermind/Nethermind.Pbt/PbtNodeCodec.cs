// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Pbt;

/// <summary>Encodes the stored node layouts.</summary>
/// <remarks>
/// A branch is its EIP-8297 hash preimage, <c>[0x01][bitCount u16 BE][prefix][left hash][right hash]</c>, followed by a
/// trailer <c>[leftKeyLength u8][rightKeyLength u8][leftKey][rightKey]</c>. A non-zero key length declares that child a
/// leaf and inlines its key past the whole bytes of the branch's group path (see <see cref="InlineKeyOffset"/>), which
/// every key under the group shares; its hash is the child hash already in the preimage, so leaves are not stored as
/// nodes. The only exception is a tree consisting of one leaf, whose root is stored as <c>[0x00][keyLength u8][key]</c>
/// without a value: its hash is the tree root, which every reader of the root group already has.
/// </remarks>
internal static class PbtNodeCodec
{
    private const byte LeafTag = 0;
    private const byte BranchTag = 1;
    internal const int BranchTrailerHeaderLength = 2;

    /// <summary>The length of a branch's hash preimage, which starts its encoding.</summary>
    internal static int BranchPreimageLength(int bitCount) => 3 + PbtBitPrefix.ByteCount(bitCount) + 64;

    /// <summary>The longest branch preimage a tree can hold: a prefix is bounded by the longest complete key.</summary>
    internal const int MaxBranchPreimageLength = 3 + PbtVariableTreeKey.MaxLength + 64;

    /// <summary>The length of a complete branch encoding.</summary>
    internal static int BranchLength(int bitCount, int leftKeyLength, int rightKeyLength) =>
        BranchPreimageLength(bitCount) + BranchTrailerHeaderLength + leftKeyLength + rightKeyLength;

    /// <summary>The number of leading key bytes an inline leaf key omits under a branch anchored at <paramref name="anchorDepth"/>.</summary>
    /// <remarks>These are the whole bytes of the path of the group holding the branch; an odd-nibble group keeps its last nibble in the key.</remarks>
    internal static int InlineKeyOffset(int anchorDepth) => PbtFourLevelGroupGeometry.GroupDepthOf(anchorDepth) >> 3;

    /// <summary>The trailer length of <paramref name="stored"/>'s inline keys once rebased from key offset <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal static int RebasedKeysLength(PbtBranchReader stored, int from, int to) =>
        RebasedKeyLength(stored.LeftKeyPostfix.Length, from, to) + RebasedKeyLength(stored.RightKeyPostfix.Length, from, to);

    private static int RebasedKeyLength(int keyLength, int from, int to) => keyLength == 0 ? 0 : keyLength + from - to;

    /// <summary>Writes <paramref name="stored"/>'s inline keys as a trailer under key offset <paramref name="to"/> instead of <paramref name="from"/>.</summary>
    /// <param name="path">A path through the branch covering the bytes a shallower offset takes back.</param>
    internal static void WriteRebasedBranchTrailer(Span<byte> trailer, PbtBranchReader stored, int from, int to, scoped ReadOnlySpan<byte> path)
    {
        ReadOnlySpan<byte> leftKey = stored.LeftKeyPostfix, rightKey = stored.RightKeyPostfix;
        int leftLength = RebasedKeyLength(leftKey.Length, from, to);
        WriteBranchTrailer(trailer, leftLength, RebasedKeyLength(rightKey.Length, from, to));
        Span<byte> keys = trailer[BranchTrailerHeaderLength..];
        RebaseKey(leftKey, keys, from, to, path);
        RebaseKey(rightKey, keys[leftLength..], from, to, path);

        static void RebaseKey(ReadOnlySpan<byte> key, Span<byte> destination, int from, int to, scoped ReadOnlySpan<byte> path)
        {
            if (key.IsEmpty) return;
            if (to >= from)
            {
                Debug.Assert(key.Length > to - from, "An inline leaf key extends past its branch.");
                key[(to - from)..].CopyTo(destination);
                return;
            }
            path[to..from].CopyTo(destination);
            key.CopyTo(destination[(from - to)..]);
        }
    }

    /// <summary>The length of <paramref name="stored"/>, anchored at <paramref name="storedAnchorDepth"/>, encoded at the deeper <paramref name="anchorDepth"/>.</summary>
    internal static int ReanchoredLength(scoped PbtBranchReader stored, int storedAnchorDepth, int anchorDepth) =>
        BranchPreimageLength(stored.Prefix.BitCount - (anchorDepth - storedAnchorDepth)) + BranchTrailerHeaderLength
        + RebasedKeysLength(stored, InlineKeyOffset(storedAnchorDepth), InlineKeyOffset(anchorDepth));

    /// <summary>Encodes <paramref name="stored"/>, anchored at <paramref name="storedAnchorDepth"/>, at the deeper <paramref name="anchorDepth"/> with the given child hashes.</summary>
    /// <remarks>The prefix bits in between are dropped, and so are any key bytes the deeper anchor's inline keys omit.</remarks>
    internal static int EncodeReanchored(scoped PbtBranchReader stored, int storedAnchorDepth, int anchorDepth, in ValueHash256 left, in ValueHash256 right, Span<byte> encoding)
    {
        CompressedPrefix prefix = stored.Prefix;
        int skippedBits = anchorDepth - storedAnchorDepth;
        int bitCount = prefix.BitCount - skippedBits;
        int length = ReanchoredLength(stored, storedAnchorDepth, anchorDepth);
        CreateBranchEncoding(encoding, bitCount, left, right);
        if (bitCount != 0) PbtBitPrefix.CopyBits(prefix.Bytes, skippedBits, bitCount, encoding[3..], 0);
        WriteRebasedBranchTrailer(encoding[BranchPreimageLength(bitCount)..length], stored,
            InlineKeyOffset(storedAnchorDepth), InlineKeyOffset(anchorDepth), default);
        return length;
    }

    /// <summary>Hashes the branch <paramref name="stored"/> re-anchored <paramref name="skippedBits"/> deeper, without its leading prefix bits.</summary>
    [SkipLocalsInit]
    internal static ValueHash256 HashReanchored(PbtBranchReader stored, int skippedBits)
    {
        if (skippedBits == 0) return Blake3Hash.Hash(stored.Preimage);
        CompressedPrefix prefix = stored.Prefix;
        int bitCount = prefix.BitCount - skippedBits;
        Span<byte> preimage = stackalloc byte[BranchPreimageLength(bitCount)];
        CreateBranchEncoding(preimage, bitCount, stored.LeftHash, stored.RightHash);
        PbtBitPrefix.CopyBits(prefix.Bytes, skippedBits, bitCount, preimage[3..], 0);
        return Blake3Hash.Hash(preimage);
    }

    /// <summary>Whether <paramref name="encoding"/> is a root leaf rather than a branch.</summary>
    internal static bool IsLeaf(ReadOnlySpan<byte> encoding) => encoding[0] == LeafTag;

    /// <summary>The root leaf's complete key.</summary>
    internal static ReadOnlySpan<byte> LeafKey(ReadOnlySpan<byte> encoding)
    {
        Debug.Assert(IsLeaf(encoding), "The PBT node is not a leaf.");
        return encoding[2..];
    }

    /// <summary>The length of a root leaf encoding.</summary>
    internal static int LeafLength(int keyLength) => 2 + keyLength;

    /// <summary>Validates a node path's canonical form, see <see cref="IsCanonicalPath"/>.</summary>
    /// <remarks>Debug builds only: release callers construct paths from already-canonical bytes.</remarks>
    [Conditional("DEBUG")]
    internal static void ValidatePath(ReadOnlySpan<byte> path, int bitDepth, int maximumDepth)
    {
        if (!IsCanonicalPath(path, bitDepth, maximumDepth)) throw new ArgumentException("Path is not in canonical form.", nameof(path));
    }

    /// <summary>Whether a node path is in canonical form: <paramref name="bitDepth"/> bits within <paramref name="maximumDepth"/>, packed into exactly as many bytes, with the unused trailing bits zero.</summary>
    /// <remarks>Checks in every build, for callers decoding untrusted keys.</remarks>
    internal static bool IsCanonicalPath(ReadOnlySpan<byte> path, int bitDepth, int maximumDepth) =>
        (uint)bitDepth <= (uint)maximumDepth
        && path.Length == PbtBitPrefix.ByteCount(bitDepth)
        && ((bitDepth & 7) == 0 || (path[^1] & (0xFF >> (bitDepth & 7))) == 0);

    /// <summary>Throws unless <paramref name="encoding"/> is exactly one structurally valid node, in every build.</summary>
    internal static void ThrowIfNotExact(ReadOnlySpan<byte> encoding)
    {
        if (encoding.IsEmpty) throw new InvalidDataException("A PBT node encoding cannot be empty.");
        if (encoding[0] is not LeafTag and not BranchTag) throw new InvalidDataException("Unknown PBT node tag.");
        if (encoding.Length < 3) throw new InvalidDataException("Truncated PBT node encoding.");

        if (encoding[0] == LeafTag)
        {
            int keyLength = encoding[1];
            if (keyLength is < 1 or > PbtVariableTreeKey.MaxLength)
                throw new InvalidDataException("Invalid PBT leaf key length.");
            if (encoding.Length != LeafLength(keyLength)) throw new InvalidDataException("Invalid PBT leaf encoding length.");
            return;
        }

        int bitCount = BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]);
        int prefixByteCount = PbtBitPrefix.ByteCount(bitCount);
        int preimageLength = BranchPreimageLength(bitCount);
        if (encoding.Length < preimageLength + BranchTrailerHeaderLength) throw new InvalidDataException("Invalid PBT branch encoding length.");
        int leftKeyLength = encoding[preimageLength];
        int rightKeyLength = encoding[preimageLength + 1];
        if (leftKeyLength > PbtVariableTreeKey.MaxLength || rightKeyLength > PbtVariableTreeKey.MaxLength)
            throw new InvalidDataException("Invalid PBT branch leaf key length.");
        if (encoding.Length != BranchLength(bitCount, leftKeyLength, rightKeyLength)) throw new InvalidDataException("Invalid PBT branch encoding length.");
        if (bitCount % 8 != 0 && (encoding[2 + prefixByteCount] & (0xFF >> (bitCount % 8))) != 0)
            throw new InvalidDataException("Invalid PBT branch prefix.");

        int leftHashOffset = 3 + prefixByteCount;
        if (encoding[leftHashOffset..(leftHashOffset + 32)].IsZero()
            || encoding[(leftHashOffset + 32)..(leftHashOffset + 64)].IsZero())
            throw new InvalidDataException("A PBT branch must have two non-empty children.");
    }

    /// <summary>The length of the EIP-8297 leaf hash preimage for a key of <paramref name="keyLength"/> bytes.</summary>
    internal static int LeafPreimageLength(int keyLength) => 1 + keyLength + 32;

    /// <summary>Writes the EIP-8297 leaf hash preimage of a complete key and 32-byte value into <paramref name="preimage"/>, which is <see cref="LeafPreimageLength"/> bytes long.</summary>
    internal static void WriteLeafPreimage(Span<byte> preimage, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        preimage[0] = LeafTag;
        key.CopyTo(preimage[1..]);
        value.CopyTo(preimage[(1 + key.Length)..]);
    }

    internal static void EncodeLeaf<TKey>(Span<byte> encoding, TKey key) where TKey : struct, IPbtKey<TKey>
    {
        encoding[0] = LeafTag;
        encoding[1] = (byte)key.Length;
        key.Bytes.CopyTo(encoding[2..]);
    }

    /// <summary>Writes a branch's hash preimage with a zeroed prefix for direct bit composition.</summary>
    /// <remarks>Only the first <see cref="BranchPreimageLength"/> bytes are written; the trailer follows through <see cref="WriteBranchTrailer"/>.</remarks>
    internal static void CreateBranchEncoding(Span<byte> encoding, int bitCount, in ValueHash256 left, in ValueHash256 right)
    {
        if ((uint)bitCount > PbtBitPrefix.MaxBitCount) throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (left == default || right == default) throw new InvalidDataException("A PBT branch must have two non-empty children.");
        int prefixLength = PbtBitPrefix.ByteCount(bitCount);
        encoding[0] = BranchTag;
        BinaryPrimitives.WriteUInt16BigEndian(encoding[1..], (ushort)bitCount);
        encoding.Slice(3, prefixLength).Clear();
        left.Bytes.CopyTo(encoding[(3 + prefixLength)..]);
        right.Bytes.CopyTo(encoding[(3 + prefixLength + 32)..]);
    }

    /// <summary>Writes the inline leaf keys, already past <see cref="InlineKeyOffset"/>, that follow a branch's preimage; an empty key declares a branch child.</summary>
    internal static void WriteBranchTrailer(Span<byte> trailer, ReadOnlySpan<byte> leftKey, ReadOnlySpan<byte> rightKey)
    {
        WriteBranchTrailer(trailer, leftKey.Length, rightKey.Length);
        leftKey.CopyTo(trailer[BranchTrailerHeaderLength..]);
        rightKey.CopyTo(trailer[(BranchTrailerHeaderLength + leftKey.Length)..]);
    }

    /// <summary>Writes the trailer's key lengths; the caller copies the keys behind them.</summary>
    internal static void WriteBranchTrailer(Span<byte> trailer, int leftKeyLength, int rightKeyLength)
    {
        if (leftKeyLength > PbtVariableTreeKey.MaxLength || rightKeyLength > PbtVariableTreeKey.MaxLength)
            throw new ArgumentException("An inline leaf key exceeds the maximum key length.");
        trailer[0] = (byte)leftKeyLength;
        trailer[1] = (byte)rightKeyLength;
    }
}
