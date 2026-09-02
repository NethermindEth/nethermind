// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Rebuilds and atomically persists a canonical EIP-8297 tree from complete-key entries.</summary>
public sealed class PbtRebuilder(PbtRocksDbPersistence target, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();

    public Task<ValueHash256> Rebuild(
        ChannelReader<ArrayPoolList<RebuildEntry>> source,
        StateId targetState,
        CancellationToken cancellationToken) => Rebuild(source, EmptyCodeReferences, targetState, cancellationToken);

    private static readonly IReadOnlyDictionary<ValueHash256, ulong> EmptyCodeReferences = new Dictionary<ValueHash256, ulong>();

    public async Task<ValueHash256> Rebuild(
        ChannelReader<ArrayPoolList<RebuildEntry>> source,
        IReadOnlyDictionary<ValueHash256, ulong> codeReferences,
        StateId targetState,
        CancellationToken cancellationToken)
    {
        SortedDictionary<PbtFullKey, ValueHash256> leaves = [];
        await foreach (ArrayPoolList<RebuildEntry> chunk in source.ReadAllAsync(cancellationToken))
        {
            using (chunk)
            {
                foreach (RebuildEntry entry in chunk.AsSpan())
                {
                    if (entry.Leaf != default) leaves[entry.Key] = entry.Leaf;
                }
            }
        }

        PbtWriteBatch changes = new();
        foreach ((PbtFullKey key, ValueHash256 value) in leaves) changes.Set(key, value);
        PbtPhysicalNodeStore nodeStore = new(PbtNodeLayout.Record);
        ValueHash256 root = TrieUpdater.UpdateRoot(nodeStore, default, changes);

        using IPbtPersistence.IWriteBatch batch = target.CreateWriteBatch(StateId.PreGenesis, targetState, root, WriteFlags.None);
        foreach ((PbtFullKey key, ValueHash256 value) in leaves) batch.SetLeaf(key, value);
        foreach (PbtNodeRecord node in nodeStore.EnumerateRecords()) batch.SetNode(node.Path, node.Encoding.Span);
        foreach ((ValueHash256 codeHash, ulong count) in codeReferences) batch.SetCodeReference(codeHash, count);
        batch.Commit();
        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at {targetState}: {leaves.Count} leaves, tree root {root}");
        return root;
    }
}
