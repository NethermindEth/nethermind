// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>
/// Writes one preimage-flat import producer's records into the scratch database: derives each entry's
/// stem and sub-index and stages the <see cref="PbtImportScratch"/> record under that tree key.
/// </summary>
/// <remarks>
/// Every producer owns its own writer and write batch, so no synchronisation is needed; the batch is
/// opened with <see cref="WriteFlags.DisableWAL"/>, under which RocksDB flushes it every few hundred
/// writes rather than growing it unboundedly. Skipping the WAL is safe because the scratch database
/// is recreated from scratch on every run.
/// </remarks>
internal sealed class PbtImportScratchWriter(IWriteBatch batch)
{
    /// <summary>
    /// Stages an account's records: its <c>BASIC_DATA</c> leaf carrying the flat row, its
    /// <c>CODE_HASH</c> leaf, its header code chunks and any overflow code chunks on their
    /// content-addressed code-zone stems. <paramref name="addressHash"/> is the account's precomputed
    /// <see cref="PbtKeyDerivation.AddressKeyHash"/> and <paramref name="slimRlp"/> its encoding as
    /// read from the flat source.
    /// </summary>
    /// <param name="emitOverflowChunks">
    /// Whether to stage the overflow code chunks. Their stems and values depend only on the code hash,
    /// so a caller that already staged this code's chunks for another account passes <c>false</c> —
    /// the header chunks are address-keyed and are always staged.
    /// </param>
    public void WriteAccount(Address address, Account account, ReadOnlySpan<byte> slimRlp, byte[]? code, in ValueHash256 addressHash, bool emitOverflowChunks)
    {
        Span<byte> value = stackalloc byte[PbtImportScratch.MaxValueLength];
        Stem headerStem = PbtKeyDerivation.AccountHeaderStem(addressHash);

        Span<byte> basicData = stackalloc byte[32];
        PbtKeyDerivation.PackBasicData(basicData, code is null ? 0u : (uint)code.Length, account.Nonce, account.Balance);
        Put(headerStem, PbtKeyDerivation.BasicDataLeafKey, value[..PbtImportScratch.EncodeAccountRow(address, slimRlp, basicData, value)]);
        WriteLeaf(headerStem, PbtKeyDerivation.CodeHashLeafKey, account.CodeHash.Bytes, value);

        if (code is not { Length: > 0 }) return;

        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        int headerChunks = Math.Min(chunkCount, PbtKeyDerivation.HeaderCodeChunks);
        for (int i = 0; i < headerChunks; i++)
        {
            WriteLeaf(headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(i), Chunk(chunks, i), value);
        }

        if (!emitOverflowChunks) return;

        // overflow chunks (index 128+) live on their own content-addressed code-zone stems, each stem
        // holding a run of up to a full stem's worth — derive the stem once per run, not per chunk
        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunkCount;)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
            int run = Math.Min(chunkCount - i, PbtKeyDerivation.StemSubtreeWidth - subIndex);
            for (int j = 0; j < run; j++)
            {
                WriteLeaf(overflowStem, (byte)(subIndex + j), Chunk(chunks, i + j), value);
            }

            i += run;
        }
    }

    /// <summary>Stages one storage slot's record on the stem <paramref name="deriver"/> routes it to.</summary>
    public void WriteSlot(Address address, in UInt256 slot, in EvmWord word, ref SlotDeriver deriver)
    {
        Stem stem = deriver.Derive(slot, out byte subIndex);
        Span<byte> value = stackalloc byte[PbtImportScratch.MaxValueLength];
        Put(stem, subIndex, value[..PbtImportScratch.EncodeSlot(address, slot, word, value)]);
    }

    private void WriteLeaf(in Stem stem, int subIndex, ReadOnlySpan<byte> leaf, Span<byte> value) =>
        Put(stem, (byte)subIndex, value[..PbtImportScratch.EncodeLeaf(leaf, value)]);

    private void Put(in Stem stem, byte subIndex, ReadOnlySpan<byte> value)
    {
        ValueHash256 key = PbtKeyDerivation.TreeKey(stem, subIndex);
        batch.PutSpan(key.Bytes, value, WriteFlags.DisableWAL);
    }

    private static ReadOnlySpan<byte> Chunk(byte[] chunks, int chunkId) =>
        chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize);

    /// <summary>
    /// Routes one address's slots to the account header (index &lt; 64) or to a storage-zone stem off a
    /// precomputed address prefix — mirroring the live path's <c>StorageWriteBatch</c> memos: the
    /// header stem is derived once, and a storage-zone stem is shared by the 256 slots of one tree
    /// index (<c>slot &gt;&gt; 8</c>), so slots arriving in ascending order reuse a single derivation
    /// per run.
    /// </summary>
    public struct SlotDeriver(Address address, ValueHash256 addressPrefix)
    {
        private Stem _headerStem;
        private bool _headerStemComputed;
        private UInt256 _lastTreeIndex;
        private Stem _lastStorageStem;
        private bool _hasStorageStem;

        public Stem Derive(in UInt256 slot, out byte subIndex)
        {
            if (PbtKeyDerivation.IsHeaderSlot(slot))
            {
                if (!_headerStemComputed)
                {
                    _headerStem = PbtKeyDerivation.AccountHeaderStem(addressPrefix);
                    _headerStemComputed = true;
                }

                subIndex = PbtKeyDerivation.HeaderSlotSubIndex(slot);
                return _headerStem;
            }

            UInt256 treeIndex = slot >> 8;
            if (_hasStorageStem && treeIndex == _lastTreeIndex)
            {
                subIndex = (byte)(slot.u0 & 0xFF);
                return _lastStorageStem;
            }

            Stem stem = PbtKeyDerivation.StorageStem(address, addressPrefix, slot, out subIndex);
            (_lastStorageStem, _lastTreeIndex, _hasStorageStem) = (stem, treeIndex, true);
            return stem;
        }
    }
}
