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
    internal static IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> AccountLeaves(ValueHash256 addressHash, Account account, CodeInfo? code, bool includeCode = true)
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
        if (code is null || !includeCode) yield break;
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

    internal static IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> EnumerateLeaves(IPbtPersistence.IReader reader) =>
        EnumerateLeaves(reader.EnumerateAccounts(), reader.EnumerateStorage(), hash => reader.GetCode(hash));

    internal static IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> EnumerateLeaves(
        IEnumerable<KeyValuePair<ValueHash256, Account>> accounts,
        IEnumerable<KeyValuePair<PbtStorageFullKey, EvmWord>> storages,
        Func<ValueHash256, CodeInfo?> getCode)
    {
        SortedDictionary<PbtStorageFullKey, ValueHash256> leaves = [];
        HashSet<ValueHash256> emittedCode = [];
        foreach ((ValueHash256 addressHash, Account account) in accounts)
            foreach ((PbtFullKey key, ValueHash256 value) in AccountLeaves(addressHash, account, account.HasCode ? getCode(account.CodeHash.ValueHash256) : null, emittedCode.Add(account.CodeHash.ValueHash256)))
                leaves[(PbtStorageFullKey)key] = value;
        foreach ((PbtStorageFullKey key, EvmWord value) in storages)
            if (!EvmWordSlot.IsZero(value)) leaves[key] = new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value));
        return leaves;
    }

    internal static ValueHash256 StorageAddress(PbtStorageFullKey key) => new(key.Bytes.Slice(1, ValueHash256.MemorySize));

    internal static void ApplyStorage(IDictionary<PbtStorageFullKey, EvmWord> visible, PbtSnapshotContent content, ValueHash256? addressFilter = null)
    {
        foreach ((ValueHash256 addressHash, _) in content.SelfDestructedStorageAddresses)
        {
            if (addressFilter is not null && addressHash != addressFilter.Value) continue;
            using ArrayPoolListRef<PbtStorageFullKey> removed = new(0);
            foreach (PbtStorageFullKey key in visible.Keys)
                if (StorageAddress(key) == addressHash) removed.Add(key);
            foreach (PbtStorageFullKey key in removed) visible.Remove(key);
        }
        foreach ((PbtStorageFullKey key, EvmWord value) in content.Storages)
            if (addressFilter is null || StorageAddress(key) == addressFilter.Value) visible[key] = value;
    }
}
