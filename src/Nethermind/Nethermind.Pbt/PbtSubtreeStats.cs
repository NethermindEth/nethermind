// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;

namespace Nethermind.Pbt;

/// <summary>
/// What a node's whole subtree amounts to, as against what the node itself holds: for now the number
/// of stems anywhere below it, which is what says how much work descending into it is.
/// </summary>
/// <remarks>
/// Carried by every stored node — a <see cref="PbtTrieNodeGroup"/> in its header, a
/// <see cref="PbtNodeChain"/> beside its cached node hash — so that a subtree's size can be read
/// without walking it, which is what lets work over the trie be split before the split is descended.
/// <para>
/// <see cref="TrieUpdater"/> maintains these as deltas hoisted up its descent rather than as counts
/// recomputed from below: a boundary slot no write touches contributes a zero delta, and its child
/// blob is never read. An absolute count would have to read every child of every group on the path.
/// That is also why one type serves both a stored value and a delta — the two only ever meet through
/// <see cref="op_Addition"/>, so a statistic added here is hoisted by the updater with no further
/// plumbing.
/// </para>
/// <para>
/// Only an absolute count is ever encoded; a delta lives in the updater's locals and never reaches a
/// blob. So the encoding is an unsigned 48-bit count, while <see cref="StemCount"/> is signed purely
/// so that a delta can be negative — a stem dies when every leaf of its blob is zeroed.
/// </para>
/// </remarks>
public readonly struct PbtSubtreeStats(long stemCount) : IEquatable<PbtSubtreeStats>
{
    private const int StemCountLength = 6;

    public const int EncodedLength = StemCountLength;

    /// <summary>The number of stems in the subtree, or the change in it when this is a delta.</summary>
    public long StemCount { get; } = stemCount;

    /// <summary>True for a delta that leaves every statistic as it was, whose consumer has nothing to do.</summary>
    public bool IsZero => StemCount == 0;

    /// <summary>The delta of a subtree gaining one stem, or the statistics of a lone stem node.</summary>
    public static PbtSubtreeStats OneStem => new(1);

    public static PbtSubtreeStats operator +(in PbtSubtreeStats left, in PbtSubtreeStats right) =>
        new(left.StemCount + right.StemCount);

    public static PbtSubtreeStats operator -(in PbtSubtreeStats left, in PbtSubtreeStats right) =>
        new(left.StemCount - right.StemCount);

    public static bool operator ==(in PbtSubtreeStats left, in PbtSubtreeStats right) => left.Equals(right);

    public static bool operator !=(in PbtSubtreeStats left, in PbtSubtreeStats right) => !left.Equals(right);

    /// <summary>Reads the statistics of <see cref="EncodedLength"/> bytes at the start of <paramref name="data"/>.</summary>
    public static PbtSubtreeStats Read(ReadOnlySpan<byte> data) =>
        new((long)(BinaryPrimitives.ReadUInt32LittleEndian(data) | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(data[sizeof(uint)..]) << 32)));

    /// <summary>Writes the statistics into the first <see cref="EncodedLength"/> bytes of <paramref name="destination"/>.</summary>
    /// <remarks>
    /// The count is split as a 32-bit and a 16-bit half so that both are native
    /// <see cref="BinaryPrimitives"/> widths, as the group's bitmap header is read and written. The
    /// halves are little-endian rather than blitted: a statistic is wider than a byte, so its layout
    /// in memory is no encoding.
    /// </remarks>
    public readonly void Write(Span<byte> destination)
    {
        Debug.Assert((ulong)StemCount >> 48 == 0, "only an absolute count is encoded, and it fits in 48 bits");

        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)StemCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[sizeof(uint)..], (ushort)(StemCount >> 32));
    }

    public readonly bool Equals(PbtSubtreeStats other) => StemCount == other.StemCount;

    public override readonly bool Equals(object? obj) => obj is PbtSubtreeStats other && Equals(other);

    public override readonly int GetHashCode() => StemCount.GetHashCode();

    public override readonly string ToString() => $"{StemCount} stems";
}
