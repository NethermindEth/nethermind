// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Pbt;

/// <summary>
/// The record format of the preimage-flat import's scratch database — the throwaway on-disk sort that
/// turns the address-ordered source into the stem-ordered stream <see cref="PbtRebuilder"/> folds
/// window by window.
/// </summary>
/// <remarks>
/// The key is the 32-byte tree key (<see cref="PbtKeyDerivation.TreeKey"/>), so the database's own
/// ordering is stem order and each leaf lands on exactly one key — duplicate records are impossible
/// by construction, and the sort itself costs nothing beyond the write. The value carries everything
/// the second phase needs, so that phase touches neither the flat source nor the code database: all
/// stem derivation, code chunking and <c>BASIC_DATA</c> packing happens once, on the first phase's
/// parallel producers.
/// </remarks>
internal static class PbtImportScratch
{
    /// <summary>The key the import's scratch database is registered under.</summary>
    public const string DbName = "pbtImportScratch";

    /// <summary>The record key: a full tree key, <see cref="Stem"/> followed by the sub-index byte.</summary>
    public const int KeyLength = Stem.Length + 1;

    private const int LeafLength = 32;
    private const int SlotLength = 32;

    /// <summary>Slim-RLP account bound: nonce, balance, storage root and code hash, each with its RLP prefix.</summary>
    private const int MaxSlimRlpLength = 128;

    /// <summary>Upper bound on an encoded value, so callers can encode into a single stack buffer.</summary>
    public const int MaxValueLength = 1 + LeafLength + Address.Size + MaxSlimRlpLength;

    private enum Kind : byte
    {
        /// <summary>A bare tree leaf: <c>CODE_HASH</c>, a header code chunk or an overflow code chunk.</summary>
        Leaf = 0,

        /// <summary>
        /// An account's <c>BASIC_DATA</c> leaf together with its flat row. The two share one record
        /// because <c>BASIC_DATA</c> is written exactly once per account and no storage slot or code
        /// chunk can land on its sub-index, so the flat row needs no key of its own.
        /// </summary>
        AccountRow = 1,

        /// <summary>A storage slot's flat row; the slot's canonical 32-byte value doubles as its leaf.</summary>
        Slot = 2,
    }

    public static int EncodeLeaf(ReadOnlySpan<byte> leaf, Span<byte> dest)
    {
        dest[0] = (byte)Kind.Leaf;
        Span<byte> padded = dest.Slice(1, LeafLength);
        padded.Clear(); // callers reuse one buffer, so a short leaf must not inherit the previous record's tail
        leaf.CopyTo(padded);
        return 1 + LeafLength;
    }

    public static int EncodeAccountRow(Address address, ReadOnlySpan<byte> slimRlp, ReadOnlySpan<byte> basicDataLeaf, Span<byte> dest)
    {
        if (slimRlp.Length > MaxSlimRlpLength) throw new InvalidDataException($"Slim account encoding for {address} is {slimRlp.Length} bytes, above the {MaxSlimRlpLength}-byte scratch record bound.");

        dest[0] = (byte)Kind.AccountRow;
        basicDataLeaf.CopyTo(dest[1..]);
        address.Bytes.CopyTo(dest[(1 + LeafLength)..]);
        slimRlp.CopyTo(dest[(1 + LeafLength + Address.Size)..]);
        return 1 + LeafLength + Address.Size + slimRlp.Length;
    }

    public static int EncodeSlot(Address address, in UInt256 slot, in EvmWord word, Span<byte> dest)
    {
        dest[0] = (byte)Kind.Slot;
        address.Bytes.CopyTo(dest[1..]);
        slot.ToBigEndian(dest.Slice(1 + Address.Size, SlotLength));
        EvmWordSlot.AsReadOnlySpan(in word).CopyTo(dest[(1 + Address.Size + SlotLength)..]);
        return 1 + Address.Size + SlotLength + LeafLength;
    }

    /// <summary>Decodes one scratch record back into the entry <see cref="PbtRebuilder"/> consumes.</summary>
    public static RebuildEntry Decode(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        Stem stem = new(key[..Stem.Length]);
        byte subIndex = key[Stem.Length];

        switch ((Kind)value[0])
        {
            case Kind.Leaf:
                return RebuildEntry.ForLeaf(stem, subIndex, new ValueHash256(value.Slice(1, LeafLength)));
            case Kind.AccountRow:
                ValueHash256 basicData = new(value.Slice(1, LeafLength));
                Address address = new(value.Slice(1 + LeafLength, Address.Size));
                RlpReader reader = new(value[(1 + LeafLength + Address.Size)..]);
                return RebuildEntry.ForAccount(address, AccountDecoder.Slim.Decode(ref reader)!, stem, subIndex, basicData);
            case Kind.Slot:
                Address slotAddress = new(value.Slice(1, Address.Size));
                UInt256 slot = new(value.Slice(1 + Address.Size, SlotLength), isBigEndian: true);
                EvmWord word = EvmWordSlot.FromStripped(value.Slice(1 + Address.Size + SlotLength, LeafLength));
                return RebuildEntry.ForSlot(slotAddress, slot, word, stem, subIndex);
            default:
                throw new InvalidDataException($"Unknown PBT import scratch record kind {value[0]}.");
        }
    }
}
