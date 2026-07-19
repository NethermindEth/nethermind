// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Builds the tree leaves of an account or slot for tests that feed <see cref="PbtRebuilder"/>
/// directly. The importer derives the same leaves from the flat columns; a test asserting the folded
/// root against <c>PbtReferenceModel</c> would catch a disagreement between the two.
/// </summary>
internal static class PbtTestLeaves
{
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
