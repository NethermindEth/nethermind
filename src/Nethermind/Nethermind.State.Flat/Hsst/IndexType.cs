// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Discriminator written as the last byte of an HSST. Selects which index strategy
/// the rest of the blob uses. New strategies get a new value; this is not a bitfield.
/// </summary>
public enum IndexType : byte
{
    /// <summary>
    /// B-tree HSST with key-after-value data-region entries; supports the streaming write
    /// API. Wire layout: see <c>Hsst/FORMAT.md</c>, "BTree variant".
    /// </summary>
    BTree = 0x01,
    /// <summary>
    /// Fixed-size key/value layout: a packed entry array with a recursive summary index.
    /// Wire layout: see <c>Hsst/FORMAT.md</c>, "PackedArray variant".
    /// </summary>
    PackedArray = 0x02,
    // 0x03 is reserved (previously ByteTagMap). Do not reuse without a wire-format bump.
    /// <summary>
    /// Byte-addressed array map where the single-byte tag is itself the array index (no tag
    /// scan). Used where the set of tag positions is fixed and known (persisted-snapshot
    /// outer column container, per-address sub-tag container). Wire layout: see
    /// <c>Hsst/FORMAT.md</c>, "DenseByteIndex variant".
    /// </summary>
    DenseByteIndex = 0x04,
    /// <summary>
    /// Fixed 2-byte key, variable value, keys-first wire shape with u16 offsets (values
    /// capped at 64 KiB). Wire layout: see <c>Hsst/FORMAT.md</c>, "TwoByteSlotValue variant".
    /// </summary>
    TwoByteSlotValue = 0x05,
    /// <summary>
    /// Wider sibling of <see cref="TwoByteSlotValue"/> with u24 offsets (~16 MiB cap), picked
    /// when the payload exceeds the u16 cap. Wire layout: see <c>Hsst/FORMAT.md</c>,
    /// "TwoByteSlotValueLarge variant".
    /// </summary>
    TwoByteSlotValueLarge = 0x06,
    /// <summary>
    /// B-tree HSST with key-first data-region entries, selected when values are large nested
    /// HSSTs; requires <c>Add(key, valueSpan)</c> (no streaming writes). Wire layout: see
    /// <c>Hsst/FORMAT.md</c>, "BTreeKeyFirst variant".
    /// </summary>
    BTreeKeyFirst = 0x07,
}
