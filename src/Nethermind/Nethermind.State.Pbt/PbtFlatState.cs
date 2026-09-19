// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Derives canonical tree leaves from whole flat values without retaining a second flat index.</summary>
internal static class PbtFlatState
{
    internal static IEnumerable<KeyValuePair<PbtPath, ValueHash256>> AccountLeaves(ValueHash256 addressHash, Account account, CodeInfo? code)
    {
        if (account.HasCode && code is null) throw new InvalidDataException($"Missing PBT bytecode for {account.CodeHash}.");
        ValueHash256 basicData = default;
        PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, (uint)(code?.Code.Length ?? 0), account.Nonce, account.Balance);
        if (basicData != default) yield return new(PbtStateKey.Account(addressHash, PbtKeyDerivation.BasicDataLeafKey), basicData);
        if (code is not null && Eip7702Constants.IsDelegatedCode(code.CodeSpan))
        {
            ValueHash256 delegation = default;
            code.CodeSpan.CopyTo(delegation.BytesAsSpan);
            yield return new(PbtStateKey.Account(addressHash, PbtKeyDerivation.DelegationLeafKey), delegation);
            yield break;
        }
        yield return new(PbtStateKey.Account(addressHash, PbtKeyDerivation.CodeHashLeafKey), account.CodeHash.ValueHash256);
        if (code is null) yield break;
        int codeLength = code.Code.Length;
        int chunkCount = (codeLength + 30) / 31;
        int chunksLength = chunkCount * PbtKeyDerivation.CodeChunkSize;
        using ArrayPoolList<byte> chunks = new(chunksLength, chunksLength);
        PbtKeyDerivation.ChunkifyCode(code.CodeSpan[..codeLength], chunks.AsSpan());
        for (int chunkId = 0; chunkId < chunkCount; chunkId++)
        {
            ValueHash256 value = new(chunks.AsSpan().Slice(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
            if (value != default) yield return new(PbtStateKey.Code(addressHash, account.CodeHash.ValueHash256, chunkId), value);
        }
    }

    internal static IEnumerable<KeyValuePair<PbtStorageTreeKey, ValueHash256>> EnumerateLeaves(IPbtPersistence.IReader reader)
    {
        return EnumerateLeaves(Accounts(), Storage(), hash => reader.GetCode(hash));

        IEnumerable<KeyValuePair<ValueHash256, Account>> Accounts()
        {
            using IPbtIterator<KeyValuePair<ValueHash256, Account>> accounts = reader.EnumerateAccounts();
            while (accounts.MoveNext()) yield return accounts.Current;
        }

        IEnumerable<KeyValuePair<PbtStorageTreeKey, EvmWord>> Storage()
        {
            using IPbtIterator<KeyValuePair<PbtStorageTreeKey, EvmWord>> storage = reader.EnumerateStorage();
            while (storage.MoveNext()) yield return storage.Current;
        }
    }

    internal static IEnumerable<KeyValuePair<PbtStorageTreeKey, ValueHash256>> EnumerateLeaves(
        IEnumerable<KeyValuePair<ValueHash256, Account>> accounts,
        IEnumerable<KeyValuePair<PbtStorageTreeKey, EvmWord>> storages,
        Func<ValueHash256, CodeInfo?> getCode)
    {
        SortedDictionary<PbtStorageTreeKey, ValueHash256> leaves = [];
        foreach ((ValueHash256 addressHash, Account account) in accounts)
            foreach ((PbtPath key, ValueHash256 value) in AccountLeaves(addressHash, account, account.HasCode ? getCode(account.CodeHash.ValueHash256) : null))
                leaves[(PbtStorageTreeKey)key] = value;
        foreach ((PbtStorageTreeKey key, EvmWord value) in storages)
            if (!EvmWordSlot.IsZero(value)) leaves[key] = new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value));
        return leaves;
    }

    internal static ValueHash256 StorageAddress(in PbtStorageTreeKey key) => new(key.Bytes.Slice(1, ValueHash256.MemorySize));

    internal static void ApplyStorage(IDictionary<PbtStorageTreeKey, EvmWord> visible, PbtSnapshotContent content, ValueHash256? addressFilter = null)
    {
        foreach ((ValueHash256 addressHash, _) in content.SelfDestructedStorageAddresses)
        {
            if (addressFilter is not null && addressHash != addressFilter.Value) continue;
            using ArrayPoolListRef<PbtStorageTreeKey> removed = new(0);
            foreach (PbtStorageTreeKey key in visible.Keys)
                if (StorageAddress(key) == addressHash) removed.Add(key);
            foreach (PbtStorageTreeKey key in removed) visible.Remove(key);
        }
        // A run is whole, so every one of its slots is written, zeros included, to mask persisted values.
        foreach ((HashedKey<PbtStorageTreeKey> runKey, ISlotRun run) in content.Storages)
        {
            if (addressFilter is not null && StorageAddress(runKey) != addressFilter.Value) continue;
            for (int index = 0; index < SlotRun.Width; index++) visible[SlotRun.SlotKey(runKey.Key, index)] = run.Get(index);
        }
    }
}
