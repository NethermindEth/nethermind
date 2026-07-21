// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// A run of single-child stem trie levels, from a nibble boundary down to the group where the trie
/// branches again: the whole run collapsed into one node, holding only where it lands and what its
/// one leaf hashes to.
/// </summary>
/// <remarks>
/// A binary trie has no extension node, so a prefix shared by one contract's stems and nothing else
/// costs a <see cref="PbtTrieNodeGroup"/> every four levels, each holding a single occupied boundary
/// slot and five internal nodes. Those nodes are pure waste: the run has one leaf, so every one of
/// them changes whenever it does, and none of them is ever read on its own. A chain stores none of
/// them, and its node hash follows by folding the target's hash back up the path (see
/// <see cref="Fold"/>). This matters most for storage, whose stems are
/// <c>1 || blake3(address)[:60] || blake3(address || treeIndex)[:187]</c> (see
/// <see cref="PbtKeyDerivation.StorageStem"/>): every stem of a contract shares 61 bits, so each
/// contract grows a spine of these from wherever it parts from other contracts down to bit 61.
/// <para>
/// A chain has no key of its own: at a hundred-odd bytes it is far too little to be worth one, so
/// the boundary slot it hangs from holds its encoding outright (see
/// <see cref="PbtTrieNodeGroup.NodeKind.Chain"/>). Its start depth is therefore that slot's depth —
/// its parent group's plus <see cref="PbtLayout.TrieNodeGroupLevelsPerGroup"/> — and is not stored, so the
/// encoding is fixed at <see cref="EncodedLength"/> bytes.
/// </para>
/// <para>
/// The encoding is the target's depth, its 31-byte path, its root hash,
/// this chain's own node hash — a cache, as an internal node's hash is, which is what lets a parent
/// group treat a chain slot as an ordinary boundary internal — the subtree's
/// <see cref="PbtSubtreeStats"/>, a cache in the same way and for the same reason, and a trailing
/// format byte, as <see cref="PbtTrieNodeGroup"/>'s encoding ends with, so that an entry says what it
/// is wherever it is read.
/// </para>
/// <para>
/// The canonical form <see cref="TrieUpdater"/> maintains, on which its descent relies:
/// <list type="number">
/// <item>a stored group has two or more occupied boundary slots, bar the root group, which holds
/// whatever it cannot hoist — a lone stem, or a spine;</item>
/// <item>a chain's target is always a stored group, never another chain: chains are maximal;</item>
/// <item>no blob exists at any key from a chain's start down to its target, that target excepted;</item>
/// <item>a chain's start depth is greater than zero.</item>
/// </list>
/// </para>
/// </remarks>
public readonly ref struct PbtNodeChain
{
    /// <summary>Version sentinel ending every encoding, validated on decode; distinct from <see cref="PbtTrieNodeGroup"/>'s, which is what tells a run's encoding from a group's wherever the two meet.</summary>
    private const byte FormatByte = 0x02;

    private const int TargetDepthOffset = 0;
    private const int TargetPathOffset = TargetDepthOffset + sizeof(byte);
    private const int TargetHashOffset = TargetPathOffset + Stem.Length;
    private const int NodeHashOffset = TargetHashOffset + HashLength;
    private const int StatsOffset = NodeHashOffset + HashLength;
    private const int FormatOffset = StatsOffset + PbtSubtreeStats.EncodedLength;
    private const int HashLength = 32;

    public const int EncodedLength = FormatOffset + sizeof(byte);

    private readonly ReadOnlySpan<byte> _data;
    private readonly int _startDepth;

    private PbtNodeChain(ReadOnlySpan<byte> data, int startDepth)
    {
        _data = data;
        _startDepth = startDepth;
    }

    /// <summary>The depth this chain starts at — its slot's depth, not part of the encoding.</summary>
    public int StartDepth => _startDepth;

    /// <summary>The depth of the group this chain lands on.</summary>
    public int TargetDepth => _data[TargetDepthOffset];

    /// <summary>The path to the target, which past <see cref="StartDepth"/> is also this chain's own single-child path.</summary>
    public Stem TargetPath => new(_data.Slice(TargetPathOffset, Stem.Length));

    /// <summary>The root hash of the group stored at <see cref="TargetDepth"/> on <see cref="TargetPath"/>.</summary>
    public ValueHash256 TargetHash => new(_data.Slice(TargetHashOffset, HashLength));

    public TrieNodeKey TargetKey => TrieNodeKey.For(TargetDepth, TargetPath);

    /// <inheritdoc cref="NodeHashOf"/>
    public ValueHash256 NodeHash => NodeHashOf(_data);

    /// <summary>
    /// What the subtree this run reaches amounts to — its target group's, a run holding no stem of its
    /// own.
    /// </summary>
    public PbtSubtreeStats Stats => PbtSubtreeStats.Read(_data[StatsOffset..]);

    /// <summary>True for an encoding ending in this type's format byte rather than <see cref="PbtTrieNodeGroup"/>'s.</summary>
    /// <remarks>
    /// An encoding is never empty — an empty group encodes to zero bytes, which the store takes as a
    /// removal — so the last byte is always there to discriminate on. Nothing in the store needs this:
    /// a run lives in its parent group's encoding, whose bitmaps say where. It is for a reader holding
    /// a node's bytes and nothing else.
    /// </remarks>
    public static bool IsChain(ReadOnlySpan<byte> data) => data.Length > 0 && data[^1] == FormatByte;

    /// <summary>
    /// This chain's node hash at its start depth, read straight out of <paramref name="data"/> without
    /// validating it, for the descent's hot path.
    /// </summary>
    public static ValueHash256 NodeHashOf(ReadOnlySpan<byte> data) => new(data.Slice(NodeHashOffset, HashLength));

    /// <summary>
    /// Folds <paramref name="targetHash"/> up the single-child path to <paramref name="startDepth"/>:
    /// the node hash of a chain landing on <paramref name="targetDepth"/>.
    /// </summary>
    /// <remarks>
    /// Each level has one child and one empty sibling, which hashes as 32 zero bytes (EIP-8297; see
    /// <see cref="Blake3Hash.HashPairOrZero"/>). Which side the child sits on is the path's bit at
    /// that level.
    /// </remarks>
    public static ValueHash256 Fold(in ValueHash256 targetHash, in Stem targetPath, int targetDepth, int startDepth)
    {
        ValueHash256 hash = targetHash;
        for (int bit = targetDepth - 1; bit >= startDepth; bit--)
        {
            hash = targetPath.GetBit(bit) == 0
                ? Blake3Hash.HashWithEmptyRight(hash)
                : Blake3Hash.HashWithEmptyLeft(hash);
        }

        return hash;
    }

    /// <summary>
    /// Writes the chain from <paramref name="startDepth"/> to the group at <paramref name="targetDepth"/>
    /// on <paramref name="targetPath"/> whose root hashes to <paramref name="targetHash"/> and whose
    /// subtree amounts to <paramref name="stats"/>, folding its node hash on the way.
    /// </summary>
    public static void Write(
        Span<byte> destination, int startDepth, int targetDepth, in Stem targetPath, in ValueHash256 targetHash,
        in PbtSubtreeStats stats)
    {
        Debug.Assert(destination.Length == EncodedLength);
        Debug.Assert(IsWellFormed(startDepth, targetDepth, targetPath, targetHash, stats));

        destination[TargetDepthOffset] = (byte)targetDepth;
        targetPath.Bytes.CopyTo(destination[TargetPathOffset..]);
        targetHash.Bytes.CopyTo(destination[TargetHashOffset..]);
        Fold(targetHash, targetPath, targetDepth, startDepth).Bytes.CopyTo(destination[NodeHashOffset..]);
        stats.Write(destination[StatsOffset..]);
        destination[FormatOffset] = FormatByte;
    }

    /// <summary>
    /// Validates a chain encoding and wraps it as a read-only view; the returned chain borrows
    /// <paramref name="data"/>.
    /// </summary>
    /// <param name="startDepth">The depth of the boundary slot <paramref name="data"/> was read from.</param>
    public static PbtNodeChain Decode(ReadOnlySpan<byte> data, int startDepth)
    {
        if (data.Length != EncodedLength) throw new InvalidDataException($"Trie node chain length {data.Length} is not {EncodedLength}");
        if (data[FormatOffset] != FormatByte) throw new InvalidDataException($"Trie node chain: unexpected format byte 0x{data[FormatOffset]:x2}");

        PbtNodeChain chain = new(data, startDepth);
        if (!IsWellFormed(startDepth, chain.TargetDepth, chain.TargetPath, chain.TargetHash, chain.Stats))
        {
            throw new InvalidDataException($"Invalid trie node chain from depth {startDepth} to {chain.TargetDepth}");
        }

        // the fold is O(targetDepth - startDepth) hashes, too dear to spend on every read of a cache
        // the updater rewrites whenever the target moves
        Debug.Assert(chain.NodeHash == Fold(chain.TargetHash, chain.TargetPath, chain.TargetDepth, startDepth), "stale cached node hash");
        return chain;
    }

    /// <summary>
    /// Whether a chain from <paramref name="startDepth"/> to <paramref name="targetDepth"/> is one the
    /// canonical form admits at all — before any question of whether it is the right one.
    /// </summary>
    /// <remarks>
    /// A chain spans at least one group and lands on a group key, so both depths are multiples of
    /// <see cref="PbtLayout.TrieNodeGroupLevelsPerGroup"/> and the target is a group depth. The start is past
    /// the root, which keeps its own spine (invariant 4). The target path is a group's, so it is
    /// zero-padded past the target, and its group is stored, so its root hash is never the empty
    /// subtree's. That group is past the root and stored, so it branches (invariant 1) — two occupied
    /// boundary slots hold a stem each at the least, which is the fewest a run can reach.
    /// </remarks>
    private static bool IsWellFormed(
        int startDepth, int targetDepth, in Stem targetPath, in ValueHash256 targetHash, in PbtSubtreeStats stats) =>
        startDepth > 0
        && startDepth < targetDepth
        && targetDepth <= PbtLayout.TrieNodeGroupMaxGroupDepth
        && startDepth % PbtLayout.TrieNodeGroupLevelsPerGroup == 0
        && targetDepth % PbtLayout.TrieNodeGroupLevelsPerGroup == 0
        && targetHash != default
        && stats.StemCount >= 2
        && TrieNodeKey.For(targetDepth, targetPath).Path == targetPath;

    public override string ToString() => $"{_startDepth}->{TargetDepth}:{TargetPath}";
}
