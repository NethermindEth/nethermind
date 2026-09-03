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
public sealed class PbtRebuilder(PbtRocksDbPersistence target, ILogManager logManager, IPbtConfig config)
{
    internal int FlushEntryInterval { get; init; } = config.ImportWindowSize > 0 ? config.ImportWindowSize : 2_000_000;
    internal int MaxWindowStems { get; init; } = PbtWriteBatch.MaxPooledStems;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();

    public async Task<ValueHash256> Rebuild(ChannelReader<ArrayPoolList<RebuildEntry>> source, StateId targetState, CancellationToken cancellationToken)
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

        PbtCanonicalBuildResult result = PbtCanonicalTree.RebuildWithNodes(leaves);
        using IPbtPersistence.IWriteBatch batch = target.CreateWriteBatch(StateId.PreGenesis, targetState, result.RootHash, WriteFlags.None);
        foreach ((PbtFullKey key, ValueHash256 value) in leaves) batch.SetLeaf(key, value);
        foreach (PbtEncodedNode node in result.Nodes) batch.SetNode(new PbtFullKey(node.LocatorEncoding.Span), node.NodeEncoding.Span);
        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at {targetState}: {leaves.Count} leaves, tree root {result.RootHash}");
        return result.RootHash;
    }
}
