// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat;

/// <summary>
/// Reference to a value stored in another persisted snapshot.
/// Used by compacted snapshots to avoid duplicating data from base snapshots.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct NodeRef(int snapshotId, int rlpDataOffset)
{
    public const int Size = 8;

    /// <summary>ID of the referenced snapshot.</summary>
    public int SnapshotId { get; } = snapshotId;

    /// <summary>
    /// Absolute byte offset of the RLP item's first byte in the referenced snapshot's HSST data.
    /// Length is recovered by parsing the RLP header (see <c>RlpHelpers.PeekNextRlpLength</c>),
    /// so the referenced index does not need to carry per-entry value-length metadata.
    ///
    /// 32-bit is sufficient because a Full persisted snapshot — the only thing a NodeRef
    /// ever points into — is always under the 2 GiB ceiling (see
    /// <see cref="PersistedSnapshots.PersistedSnapshotBuilder"/> class doc and
    /// <see cref="PersistedSnapshots.PersistedSnapshotRepository.ConvertSnapshotToPersistedSnapshot"/>).
    /// Any byte past 2 GiB would be unreachable from this offset, which is why
    /// <c>ConvertFullToLinked</c> asserts the source-snapshot size up front and
    /// throws with snapshot identity if violated.
    /// </summary>
    public int RlpDataOffset { get; } = rlpDataOffset;

    public bool IsEmpty => SnapshotId == 0 && RlpDataOffset == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NodeRef Read(ReadOnlySpan<byte> data)
    {
        int sid = BinaryPrimitives.ReadInt32LittleEndian(data);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        return new NodeRef(sid, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> data, in NodeRef nodeRef)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data, nodeRef.SnapshotId);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], nodeRef.RlpDataOffset);
    }
}
