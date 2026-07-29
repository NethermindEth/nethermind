// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Builds the tree leaves of an account or slot for tests that feed <see cref="PbtRebuilder"/>
/// directly, and reads them back the way the state does. The importer derives the same leaves from the
/// source; a test asserting the folded root against <c>PbtReferenceModel</c> would catch a disagreement
/// between the two.
/// </summary>
internal static class PbtTestLeaves
{
    /// <summary>The account a persisted state holds, decoded out of its header stem's blob as every read path does.</summary>
    public static Account? ReadAccount(IPbtPersistence.IReader reader, Address address)
    {
        using RefCountingMemory? blob = reader.GetLeafBlob(PbtKeyDerivation.AccountHeaderStem(address));
        return blob is null ? null : PbtLeafDecoder.DecodeAccount(blob.GetSpan());
    }

    /// <summary>The slot value a persisted state holds, decoded out of its stem's blob at its sub-index.</summary>
    public static EvmWord ReadSlot(IPbtPersistence.IReader reader, Address address, in UInt256 slot)
    {
        using RefCountingMemory? blob = reader.GetLeafBlob(PbtLeafDecoder.SlotStem(address, slot, out byte subIndex));
        return blob is null ? default : PbtLeafDecoder.DecodeSlot(blob.GetSpan(), subIndex);
    }

    public static void AddAccount(List<RebuildEntry> into, Address address, in Account account, byte[]? code)
    {
        Stem headerStem = PbtKeyDerivation.AccountHeaderStem(address);

        ValueHash256 basicData = default;
        PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, code is null ? 0u : (uint)code.Length, account.Nonce, account.Balance);
        into.Add(new RebuildEntry(headerStem, PbtKeyDerivation.BasicDataLeafKey, basicData));
        into.Add(new RebuildEntry(headerStem, PbtKeyDerivation.CodeHashLeafKey, account.CodeHash.ValueHash256));

        if (code is not { Length: > 0 }) return;

        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        int headerChunks = Math.Min(chunkCount, PbtKeyDerivation.HeaderCodeChunks);
        for (int i = 0; i < headerChunks; i++)
        {
            into.Add(new RebuildEntry(headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(i), Chunk(chunks, i)));
        }

        for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunkCount;)
        {
            Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash.ValueHash256, i, out byte subIndex);
            int run = Math.Min(chunkCount - i, PbtKeyDerivation.StemSubtreeWidth - subIndex);
            for (int j = 0; j < run; j++)
            {
                into.Add(new RebuildEntry(overflowStem, (byte)(subIndex + j), Chunk(chunks, i + j)));
            }

            i += run;
        }
    }

    public static void AddSlot(List<RebuildEntry> into, Address address, in UInt256 slot, in UInt256 value)
    {
        PbtSlotKeyDeriver deriver = new(address);
        Stem stem = deriver.Derive(slot, out byte subIndex);
        into.Add(new RebuildEntry(stem, subIndex, new ValueHash256(value.ToBigEndian())));
    }

    /// <summary>Lays <paramref name="leaves"/> out as one stem's leaves-only blob, the way a bulk load writes one.</summary>
    /// <param name="leaves">Sub-index and its value, which is left-padded to the 32-byte leaf as the storage columns hand them over.</param>
    public static byte[] Blob(params (byte SubIndex, byte[] Value)[] leaves)
    {
        IPbtStemChanges changes = PbtStemChanges.Rent();
        foreach ((byte subIndex, byte[] value) in leaves)
        {
            ValueHash256 leaf = default;
            value.CopyTo(leaf.BytesAsSpan[(ValueHash256.MemorySize - value.Length)..]);
            changes = changes.Set(subIndex, leaf);
        }

        byte[] blob = StemLeafBlob.ApplyNoHash([], changes);
        PbtStemChanges.Return(changes);
        return blob;
    }

    /// <summary>Orders leaves by tree key, the order the importer emits them in.</summary>
    public static void SortByTreeKey(List<RebuildEntry> leaves) =>
        leaves.Sort(static (a, b) =>
        {
            int byStem = a.Stem.Bytes.SequenceCompareTo(b.Stem.Bytes);
            return byStem != 0 ? byStem : a.SubIndex.CompareTo(b.SubIndex);
        });

    private static ValueHash256 Chunk(byte[] chunks, int chunkId) =>
        new(chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
}
