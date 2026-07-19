// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>
/// One state entry to fold into the rebuilt tree: an account's flat row, a storage slot's flat row
/// and tree leaf, or a bare tree leaf (account header data and code chunks). Stems are derived by
/// the producer — <see cref="EmitAccount"/> and <see cref="SlotDeriver"/> — so the derivation's
/// BLAKE3 hashing runs on the parallel source readers rather than on the rebuilder's single
/// consumer thread.
/// </summary>
/// <remarks>
/// Arrival order is irrelevant: every (stem, sub-index) is written exactly once during an import
/// (the account header leaves, header-region slots and code chunks sharing a header stem occupy
/// disjoint sub-index bands), and the rebuilder merges a stem's leaves whatever order they land in.
/// </remarks>
public readonly struct RebuildEntry
{
    public enum EntryKind : byte
    {
        /// <summary>An account's flat row: <see cref="Address"/> and <see cref="Account"/>.</summary>
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

    public static RebuildEntry ForAccount(Address address, Account account) =>
        new(EntryKind.Account, address, account, default, default, default, 0, default);

    public static RebuildEntry ForSlot(Address address, in UInt256 slot, in EvmWord word, in Stem stem, byte subIndex) =>
        new(EntryKind.Slot, address, null, slot, word, stem, subIndex, default);

    public static RebuildEntry ForLeaf(in Stem stem, byte subIndex, in ValueHash256 leaf) =>
        new(EntryKind.Leaf, null, null, default, default, stem, subIndex, leaf);

    /// <summary>
    /// Emits an account's entries into <paramref name="sink"/>: its flat row, its EIP-8297 header
    /// leaves (BASIC_DATA, CODE_HASH, header code chunks) and any overflow code chunks on their
    /// content-addressed code-zone stems. <paramref name="addressHash"/> is the account's
    /// precomputed <see cref="PbtKeyDerivation.AddressKeyHash"/>.
    /// </summary>
    internal static void EmitAccount(Address address, Account account, byte[]? code, in ValueHash256 addressHash, ArrayPoolList<RebuildEntry> sink)
    {
        sink.Add(ForAccount(address, account));

        Stem headerStem = PbtKeyDerivation.AccountHeaderStem(addressHash);
        Span<byte> basicData = stackalloc byte[32];
        PbtKeyDerivation.PackBasicData(basicData, code is null ? 0u : (uint)code.Length, account.Nonce, account.Balance);
        sink.Add(ForLeaf(headerStem, PbtKeyDerivation.BasicDataLeafKey, ToLeaf(basicData)));
        sink.Add(ForLeaf(headerStem, PbtKeyDerivation.CodeHashLeafKey, ToLeaf(account.CodeHash.Bytes)));

        if (code is not { Length: > 0 }) return;

        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        int headerChunks = Math.Min(chunkCount, PbtKeyDerivation.HeaderCodeChunks);
        for (int i = 0; i < headerChunks; i++)
        {
            sink.Add(ForLeaf(headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(i), ToLeaf(Chunk(chunks, i))));
        }

        // overflow chunks (index 128+) live on their own content-addressed code-zone stems, each stem
        // holding a run of up to a full stem's worth — derive the stem once per run, not per chunk
        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunkCount;)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
            int run = Math.Min(chunkCount - i, PbtKeyDerivation.StemSubtreeWidth - subIndex);
            for (int j = 0; j < run; j++)
            {
                sink.Add(ForLeaf(overflowStem, (byte)(subIndex + j), ToLeaf(Chunk(chunks, i + j))));
            }

            i += run;
        }
    }

    private static ReadOnlySpan<byte> Chunk(byte[] chunks, int chunkId) =>
        chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize);

    private static ValueHash256 ToLeaf(ReadOnlySpan<byte> value)
    {
        ValueHash256 leaf = default;
        value.CopyTo(leaf.BytesAsSpan);
        return leaf;
    }

    /// <summary>
    /// Derives one address's slot entries, routing each slot to the account header (index &lt; 64)
    /// or a storage-zone stem off a precomputed address prefix — mirroring the live path's
    /// <c>StorageWriteBatch</c> memos: the header stem is derived once, and a storage-zone stem is
    /// shared by the 256 slots of one tree index (<c>slot &gt;&gt; 8</c>), so slots arriving in
    /// ascending order reuse a single derivation per run.
    /// </summary>
    internal struct SlotDeriver(Address address, ValueHash256 addressPrefix)
    {
        private Stem _headerStem;
        private bool _headerStemComputed;
        private UInt256 _lastTreeIndex;
        private Stem _lastStorageStem;
        private bool _hasStorageStem;

        public RebuildEntry Derive(in UInt256 slot, in EvmWord word)
        {
            Stem stem;
            byte subIndex;
            if (PbtKeyDerivation.IsHeaderSlot(slot))
            {
                if (!_headerStemComputed)
                {
                    _headerStem = PbtKeyDerivation.AccountHeaderStem(addressPrefix);
                    _headerStemComputed = true;
                }

                stem = _headerStem;
                subIndex = PbtKeyDerivation.HeaderSlotSubIndex(slot);
            }
            else
            {
                UInt256 treeIndex = slot >> 8;
                if (_hasStorageStem && treeIndex == _lastTreeIndex)
                {
                    stem = _lastStorageStem;
                    subIndex = (byte)(slot.u0 & 0xFF);
                }
                else
                {
                    stem = PbtKeyDerivation.StorageStem(address, addressPrefix, slot, out subIndex);
                    (_lastStorageStem, _lastTreeIndex, _hasStorageStem) = (stem, treeIndex, true);
                }
            }

            return ForSlot(address, slot, word, stem, subIndex);
        }
    }
}
