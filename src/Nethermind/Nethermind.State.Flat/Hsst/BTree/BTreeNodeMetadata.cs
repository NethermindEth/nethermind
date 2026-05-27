// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Metadata describing the format of an index node to build.
/// </summary>
internal struct BTreeNodeMetadata
{
    /// <summary>Which kind of addressable thing this is.</summary>
    /// <remarks>
    /// Encoded in the low 2 bits of the on-disk <c>Flags</c> byte. The writer emits only
    /// <see cref="BTreeNodeKind.Intermediate"/>; <see cref="BTreeNodeKind.Entry"/> is the
    /// kind used by data-region entry records and is not written here.
    /// </remarks>
    public BTreeNodeKind NodeKind;

    /// <summary>0=Variable, 1=Uniform.</summary>
    public int KeyType;
    /// <summary>
    /// Base offset subtracted from values before writing. 0 means no base offset.
    /// When non-zero, caller must subtract this from each value before calling AddKey.
    /// Encoded on disk as a fixed 6-byte LE field (max 2^48 − 1 ≈ 256 TiB).
    /// </summary>
    public ulong BaseOffset;
    /// <summary>
    /// Uniform: fixed key length or slot size.
    /// Variable: ignored.
    /// </summary>
    public int KeySlotSize;
    /// <summary>
    /// Fixed value size in bytes. The on-disk Flags byte encodes the slot width in 2 bits
    /// (bits 3-4), so only the four widths <c>{2, 3, 4, 6}</c> are valid; the writer rejects
    /// anything else. B-tree index nodes always use Uniform values; there is no
    /// Variable-value shape. Default: 4 bytes.
    /// </summary>
    public int ValueSlotSize = 4;
    /// <summary>
    /// When true, fixed-width key slots are written byte-reversed on disk so that an x86
    /// little-endian integer load of a slot equals its semantic numeric/lex value. The SIMD
    /// floor scan can then drop the per-lane byte-swap shuffle. Honored only for Uniform with
    /// <see cref="KeySlotSize"/> ∈ {2,4,8}; ignored for other shapes. Encoded as Flags bit 6
    /// in the on-disk header.
    /// </summary>
    public bool IsKeyLittleEndian = false;

    public BTreeNodeMetadata() => NodeKind = BTreeNodeKind.Intermediate;
}
