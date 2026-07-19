// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>
/// One state entry to fold into the rebuilt tree: an account's flat row and <c>BASIC_DATA</c> leaf, a
/// storage slot's flat row and tree leaf, or a bare tree leaf (<c>CODE_HASH</c> and code chunks).
/// </summary>
/// <remarks>
/// Entries reach <see cref="PbtRebuilder"/> in ascending tree-key order, because they are decoded from
/// the import's stem-ordered scratch database (<see cref="PbtImportScratch"/>). That ordering is what
/// keeps each rebuild window a contiguous stem range, so a stem is folded once instead of being
/// read-modify-written by every window that happens to contain one of its leaves.
/// </remarks>
public readonly struct RebuildEntry
{
    public enum EntryKind : byte
    {
        /// <summary>An account's flat row and its <c>BASIC_DATA</c> leaf: <see cref="Address"/>, <see cref="Account"/>, <see cref="Stem"/>, <see cref="SubIndex"/> and <see cref="Leaf"/>.</summary>
        Account,

        /// <summary>A slot's flat row and its tree leaf: <see cref="Address"/>, <see cref="Slot"/>, <see cref="Word"/>, <see cref="Stem"/> and <see cref="SubIndex"/>. The leaf value is <see cref="Word"/> itself.</summary>
        Slot,

        /// <summary>A bare tree leaf: <see cref="Stem"/>, <see cref="SubIndex"/> and <see cref="Leaf"/>.</summary>
        Leaf,
    }

    private RebuildEntry(EntryKind kind, Address? address, Account? account, in UInt256 slot, in EvmWord word, in Stem stem, byte subIndex, in ValueHash256 leaf)
    {
        Kind = kind;
        Address = address;
        Account = account;
        Slot = slot;
        Word = word;
        Stem = stem;
        SubIndex = subIndex;
        Leaf = leaf;
    }

    public EntryKind Kind { get; }
    public Address? Address { get; }
    public Account? Account { get; }
    public UInt256 Slot { get; }

    /// <summary>The slot's canonical 32-byte value, doubling as its tree leaf (a zero word and an empty leaf are the same 32 zero bytes).</summary>
    public EvmWord Word { get; }

    public Stem Stem { get; }
    public byte SubIndex { get; }
    public ValueHash256 Leaf { get; }

    public static RebuildEntry ForAccount(Address address, Account account, in Stem stem, byte subIndex, in ValueHash256 basicData) =>
        new(EntryKind.Account, address, account, default, default, stem, subIndex, basicData);

    public static RebuildEntry ForSlot(Address address, in UInt256 slot, in EvmWord word, in Stem stem, byte subIndex) =>
        new(EntryKind.Slot, address, null, slot, word, stem, subIndex, default);

    public static RebuildEntry ForLeaf(in Stem stem, byte subIndex, in ValueHash256 leaf) =>
        new(EntryKind.Leaf, null, null, default, default, stem, subIndex, leaf);
}
