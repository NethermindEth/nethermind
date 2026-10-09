// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

internal static class PbtStateKey
{
    /// <summary>BLAKE3 of the 32-byte left-padded address; the flat account/storage column key.</summary>
    public static ValueHash256 AddressKeyHash(Address address)
    {
        ValueHash256 address32 = address.ToHash();
        return Blake3Hash.Hash(address32.Bytes);
    }

    public static PbtPath Account(in ValueHash256 addressHash, byte subIndex) =>
        Eip8297KeyDerivation.AccountKey(addressHash, subIndex);

    public static PbtPath Code(in ValueHash256 codeHash, int chunkId) =>
        Eip8297KeyDerivation.OverflowCodeKey(codeHash.Bytes, chunkId);

    /// <summary>Whether <paramref name="slot"/> is a header slot, which <see cref="HeaderStorage"/> keys; others are keyed by <see cref="Storage(Address, in ValueHash256, in UInt256)"/>.</summary>
    public static bool IsHeaderSlot(in UInt256 slot) => slot < PbtKeyDerivation.HeaderStorageSlots;

    public static PbtPath HeaderStorage(in ValueHash256 addressHash, in UInt256 slot) =>
        Eip8297KeyDerivation.HeaderStorageKey(addressHash, slot);

    /// <summary>The non-header storage leaf key; takes the precomputed <see cref="AddressKeyHash"/>.</summary>
    public static PbtStoragePath Storage(Address address, in ValueHash256 addressHash, in UInt256 slot)
    {
        ValueHash256 address32 = address.ToHash();
        return Eip8297KeyDerivation.StorageKey(address32.Bytes, addressHash, slot);
    }

    /// <summary>The leaf key of any slot, header or not, for leaf streams that mix zones.</summary>
    public static PbtVariableTreeKey Slot(Address address, in ValueHash256 addressHash, in UInt256 slot) => IsHeaderSlot(slot)
        ? (PbtVariableTreeKey)HeaderStorage(addressHash, slot)
        : (PbtVariableTreeKey)Storage(address, addressHash, slot);

    internal static ValueHash256 StorageAddress<TKey>(in TKey key) where TKey : struct, IPbtKey<TKey> => new(key.Bytes.Slice(1, ValueHash256.MemorySize));

    /// <summary>The canonical tree leaves of an account, derived from its whole flat value.</summary>
    internal static IEnumerable<KeyValuePair<PbtPath, ValueHash256>> AccountLeaves(ValueHash256 addressHash, Account account, CodeInfo? code)
    {
        PbtAccount stem = PbtAccount.From(account, code);
        if (stem.BasicData != default) yield return new(Account(addressHash, PbtKeyDerivation.BasicDataLeafKey), stem.BasicData);
        if (stem.IsDelegation)
        {
            yield return new(Account(addressHash, PbtKeyDerivation.DelegationLeafKey), stem.CodeLeaf);
            yield break;
        }
        yield return new(Account(addressHash, PbtKeyDerivation.CodeHashLeafKey), stem.CodeLeaf);
        if (code is null) yield break;
        foreach (KeyValuePair<PbtPath, ValueHash256> leaf in CodeLeaves(stem.CodeLeaf, code)) yield return leaf;
    }

    /// <summary>The code-chunk leaves of <paramref name="code"/>, omitting all-zero chunks as the tree does.</summary>
    internal static IEnumerable<KeyValuePair<PbtPath, ValueHash256>> CodeLeaves(ValueHash256 codeHash, CodeInfo code)
    {
        using RefCountingMemory chunks = PbtKeyDerivation.ChunkifyCode(code.CodeSpan);
        int chunkCount = chunks.GetSpan().Length / PbtKeyDerivation.CodeChunkSize;
        for (int chunkId = 0; chunkId < chunkCount; chunkId++)
        {
            ValueHash256 value = new(chunks.GetSpan().Slice(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
            if (value != default) yield return new(Code(codeHash, chunkId), value);
        }
    }
}
