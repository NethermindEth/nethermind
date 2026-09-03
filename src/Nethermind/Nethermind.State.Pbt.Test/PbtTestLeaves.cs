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
        ValueHash256? basic = reader.GetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey));
        ValueHash256? codeHash = reader.GetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.CodeHashLeafKey));
        if (basic is null && codeHash is null) return null;
        PbtKeyDerivation.UnpackBasicData((basic ?? default).Bytes, out ulong nonce, out UInt256 balance);
        return new Account(nonce, balance, Keccak.EmptyTreeHash, codeHash is null ? Keccak.OfAnEmptyString : new Hash256(codeHash.Value.Bytes));
    }

    public static EvmWord ReadSlot(IPbtPersistence.IReader reader, Address address, in UInt256 slot)
    {
        ValueHash256? value = reader.GetLeaf(PbtStateKey.Storage(address, slot));
        return value is null ? default : EvmWordSlot.FromStripped(value.Value.Bytes);
    }

    public static void AddAccount(List<RebuildEntry> into, Address address, in Account account, byte[]? code)
    {
        ValueHash256 basicData = default;
        PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, code is null ? 0u : (uint)code.Length, account.Nonce, account.Balance);
        into.Add(new RebuildEntry(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey), basicData));
        into.Add(new RebuildEntry(PbtStateKey.Account(address, PbtKeyDerivation.CodeHashLeafKey), account.CodeHash.ValueHash256));

        if (code is not { Length: > 0 }) return;

        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        for (int i = 0; i < chunkCount; i++)
        {
            into.Add(new RebuildEntry(PbtStateKey.Code(address, account.CodeHash.ValueHash256, i), Chunk(chunks, i)));
        }
    }

    public static void AddSlot(List<RebuildEntry> into, Address address, in UInt256 slot, in UInt256 value) =>
        into.Add(new RebuildEntry(PbtStateKey.Storage(address, slot), new ValueHash256(value.ToBigEndian())));

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

    /// <summary>Orders leaves by their complete EIP-8297 keys.</summary>
    public static void SortByTreeKey(List<RebuildEntry> leaves) =>
        leaves.Sort(static (a, b) => a.Key.CompareTo(b.Key));

    private static ValueHash256 Chunk(byte[] chunks, int chunkId) =>
        new(chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
}
