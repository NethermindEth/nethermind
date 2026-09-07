// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Rebuilds and atomically persists a canonical EIP-8297 tree from logical flat-state entries.</summary>
public sealed class PbtRebuilder(PbtRocksDbPersistence target, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();

    public async Task<ValueHash256> Rebuild(
        ChannelReader<ArrayPoolList<RebuildEntry>> source,
        StateId targetState,
        CancellationToken cancellationToken)
    {
        Dictionary<ValueHash256, Account?> accounts = [];
        Dictionary<PbtFullKey, EvmWord> storages = [];
        Dictionary<ValueHash256, CodeInfo> codes = [];
        await foreach (ArrayPoolList<RebuildEntry> chunk in source.ReadAllAsync(cancellationToken))
        {
            using (chunk)
            {
                foreach (RebuildEntry entry in chunk.AsSpan())
                {
                    switch (entry.Kind)
                    {
                        case RebuildEntry.EntryKind.Account: accounts[entry.Hash] = entry.Account; break;
                        case RebuildEntry.EntryKind.Storage: storages[entry.Key] = entry.Slot; break;
                        case RebuildEntry.EntryKind.Code: codes[entry.Hash] = entry.Code!; break;
                    }
                }
            }
        }

        SortedDictionary<PbtFullKey, ValueHash256> leaves = [];
        Dictionary<ValueHash256, ulong> codeReferences = [];
        foreach ((ValueHash256 addressHash, Account? account) in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (account is null) continue;
            CodeInfo? code = null;
            if (account.HasCode)
            {
                ValueHash256 codeHash = account.CodeHash.ValueHash256;
                if (!codes.TryGetValue(codeHash, out code))
                    throw new InvalidDataException($"Missing bytecode for account {addressHash} (code hash {codeHash}).");
                codeReferences.TryGetValue(codeHash, out ulong count);
                codeReferences[codeHash] = checked(count + 1);
            }
            foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressHash, account, code, !account.HasCode || codeReferences[account.CodeHash.ValueHash256] == 1))
                if (value != default) leaves[key] = value;
        }
        foreach ((PbtFullKey key, EvmWord slot) in storages)
        {
            ValueHash256 value = new(EvmWordSlot.AsReadOnlySpan(slot));
            if (value != default) leaves[key] = value;
        }

        PbtWriteBatch changes = new();
        foreach ((PbtFullKey key, ValueHash256 value) in leaves) changes.Set(key, value);
        using PbtNodeGroupStore nodeStore = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(nodeStore, default, changes);

        using IPbtPersistence.IWriteBatch batch = target.CreateWriteBatch(StateId.PreGenesis, targetState, root, WriteFlags.None);
        foreach ((ValueHash256 hash, Account? account) in accounts) batch.SetAccount(hash, account);
        foreach ((PbtFullKey key, EvmWord slot) in storages) batch.SetSlot(key, slot);
        foreach ((ValueHash256 hash, CodeInfo code) in codes) batch.SetCode(hash, code);
        foreach (PbtNodePath groupKey in nodeStore.EnumerateNodeGroupKeys())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RefCountingMemory? payload = nodeStore.GetNodeGroup(groupKey);
            batch.SetNodeGroup(groupKey, payload);
        }
        foreach ((ValueHash256 codeHash, ulong count) in codeReferences) batch.SetCodeReference(codeHash, count);
        batch.Commit();
        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at {targetState}: {leaves.Count} leaves, tree root {root}");
        return root;
    }
}
