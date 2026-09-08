// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Rebuilds a canonical EIP-8297 tree in bounded staging windows, then publishes its state.</summary>
public sealed class PbtRebuilder(PbtRocksDbPersistence target, ILogManager logManager)
{
    private const int DefaultWindowSize = 2_000_000;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();

    /// <summary>Folds leaf records into staged tree groups and publishes the completed root.</summary>
    /// <param name="source">Owned leaf chunks; account code-hash leaves must occur once per account.</param>
    /// <param name="targetState">The source state identity to publish on success.</param>
    /// <param name="cancellationToken">Cancels consumption and tree updates before publication.</param>
    /// <param name="windowSize">Maximum received records per update; zero uses 2,000,000 records.</param>
    /// <returns>The completed canonical tree root.</returns>
    public async Task<ValueHash256> Rebuild(
        ChannelReader<ArrayPoolList<RebuildEntry>> source,
        StateId targetState,
        CancellationToken cancellationToken,
        int windowSize = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowSize);
        if (windowSize == 0) windowSize = DefaultWindowSize;

        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
        Dictionary<ValueHash256, ulong> codeReferences = [];
        ValueHash256 root = default;
        int windowCount = 0;
        long receivedCount = 0;
        long committedWindows = 0;
        Stopwatch progress = Stopwatch.StartNew();

        await foreach (ArrayPoolList<RebuildEntry> chunk in source.ReadAllAsync(cancellationToken))
        {
            using (chunk)
            {
                foreach (RebuildEntry entry in chunk.AsSpan())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Key.Length == Eip8297KeyDerivation.AccountKeyLength &&
                        entry.Key.Bytes[0] == Eip8297KeyDerivation.AccountZone &&
                        entry.Key.Bytes[^1] == PbtKeyDerivation.CodeHashLeafKey &&
                        entry.Leaf != Keccak.OfAnEmptyString.ValueHash256)
                    {
                        codeReferences.TryGetValue(entry.Leaf, out ulong count);
                        codeReferences[entry.Leaf] = checked(count + 1);
                    }
                    changes.Set(entry.Key, entry.Leaf);
                    receivedCount++;
                    if (++windowCount == windowSize) CommitWindow();
                }
            }
        }

        if (windowCount != 0) CommitWindow();
        cancellationToken.ThrowIfCancellationRequested();
        target.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        using IPbtPersistence.IWriteBatch batch = target.CreateWriteBatch(StateId.PreGenesis, targetState, root, WriteFlags.None);
        batch.Commit();
        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at {targetState}: {receivedCount} received leaves in {committedWindows} windows, tree root {root}");
        return root;

        void CommitWindow()
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (IPbtPersistence.IReader reader = target.CreateReader())
            using (IPbtPersistence.IWriteBatch stagingBatch = target.CreateStagingWriteBatch(WriteFlags.DisableWAL))
            using (PbtWriteBatch<PbtStorageFullKey> prepared = changes.Build())
            {
                root = TrieUpdater.UpdateRoot(new WindowStore(reader, stagingBatch, cancellationToken), root, prepared);
                foreach ((ValueHash256 codeHash, ulong count) in codeReferences)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stagingBatch.SetCodeReference(codeHash, checked(reader.GetCodeReference(codeHash) + count));
                }
                cancellationToken.ThrowIfCancellationRequested();
                stagingBatch.Commit();
            }
            changes.Reset();
            codeReferences.Clear();
            windowCount = 0;
            committedWindows++;
            if (progress.Elapsed >= TimeSpan.FromSeconds(10))
            {
                if (_logger.IsInfo) _logger.Info($"PBT rebuild: {receivedCount} received leaves folded in {committedWindows} windows");
                progress.Restart();
            }
        }
    }

    private sealed class WindowStore(
        IPbtPersistence.IReader reader,
        IPbtPersistence.IWriteBatch batch,
        CancellationToken cancellationToken) : IPbtStore
    {
        public RefCountingMemory? GetNodeGroup(IPbtNodePath groupKey)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return reader.GetNodeGroup(groupKey);
        }

        public void SetNodeGroup(IPbtNodePath groupKey, RefCountingMemory? payload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.SetNodeGroup(groupKey, payload);
        }
    }
}
