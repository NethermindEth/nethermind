// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class ForwardCommitmentCapture(
    IColumnsDb<FlatHistoryColumns> history,
    CommitmentDepthPolicy policy,
    CommitmentMetadata metadata,
    ArchiveProofSettings settings,
    ILogManager logManager)
{
    public const int MaxBufferedBlocks = 4096;

    private readonly ILogger _logger = logManager.GetClassLogger<ForwardCommitmentCapture>();
    private readonly SortedDictionary<ulong, CapturedBlock> _buffered = [];
    private bool _stopped;

    public bool Enabled => settings.BuildEnabled;

    public void Capture(ulong block, Snapshot snapshot)
    {
        if (!Enabled || _stopped) return;

        try
        {
            CapturedBlock captured = new(snapshot.StateNodesCount, snapshot.StorageNodesCount);
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> entry in snapshot.StateNodes)
            {
                captured.Accounts.Add(new NodeChange(default, entry.Key.Key, entry.Value.FullRlp.ToArray() ?? []));
            }

            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> entry in snapshot.StorageNodes)
            {
                (Hash256 accountPath, TreePath path) = entry.Key.Key;
                captured.Storages.Add(new NodeChange(new ValueHash256(accountPath.Bytes), path, entry.Value.FullRlp.ToArray() ?? []));
            }

            Buffer(block, captured);
        }
        catch (Exception e)
        {
            Stop(e);
        }
    }

    public void Capture(ulong block, WholeReadScanner scanner)
    {
        if (!Enabled || _stopped) return;

        try
        {
            CapturedBlock captured = new(0, 0);
            foreach (WholeReadScanner.StateNodeEntry entry in scanner.StateNodes)
            {
                captured.Accounts.Add(new NodeChange(default, entry.Path, entry.Rlp.ToArray()));
            }

            foreach (WholeReadScanner.StorageNodeEntry entry in scanner.StorageNodes)
            {
                captured.Storages.Add(new NodeChange(entry.AddressHash, entry.Path, entry.Rlp.ToArray()));
            }

            Buffer(block, captured);
        }
        catch (Exception e)
        {
            Stop(e);
        }
    }

    public void Discard() => _buffered.Clear();

    public void Complete()
    {
        if (_buffered.Count == 0) return;

        try
        {
            Replay();
        }
        catch (Exception e)
        {
            Stop(e);
        }
        finally
        {
            _buffered.Clear();
        }
    }

    private void Buffer(ulong block, CapturedBlock captured)
    {
        if (_buffered.Count >= MaxBufferedBlocks)
        {
            _buffered.Clear();
            _stopped = true;
            if (_logger.IsWarn) _logger.Warn(
                $"Archive proof commitment capture at the tip stopped: a single capture round spans more than {MaxBufferedBlocks} blocks. The retrofit walk covers that range instead.");
            return;
        }

        _buffered[block] = captured;
    }

    private void Stop(Exception e)
    {
        _stopped = true;
        _buffered.Clear();
        if (_logger.IsError) _logger.Error("Archive proof commitment capture at the tip failed and is stopped until restart; the retrofit walk can rebuild the missing range.", e);
    }

    private void Replay()
    {
        ulong first = _buffered.Keys.First();
        ulong last = _buffered.Keys.Last();

        EnsureStamp();
        using (CommitmentEmitter emitter = CommitmentEmitter.ForTip(history, policy))
        {
            foreach ((ulong block, CapturedBlock captured) in _buffered)
            {
                emitter.BeginBlock(block);
                foreach (NodeChange change in captured.Accounts) emitter.RecordAccountNode(change.Path, change.Rlp);
                foreach (NodeChange change in captured.Storages) emitter.RecordStorageNode(change.Scope, change.Path, change.Rlp);
                emitter.CompleteBlock();
            }
        }

        metadata.AdvanceTipSeries(first, last, out bool restarted);
        if (restarted && _logger.IsWarn) _logger.Warn(
            $"Archive proof commitments at the tip resume at block {first} after a gap; the series restarts there and the gap is left to the retrofit walk.");
    }

    private void EnsureStamp()
    {
        if (metadata.TryReadStamp(policy, out bool matches))
        {
            if (matches) return;

            throw new InvalidConfigurationException(
                $"The archive proof commitment columns were written under a different layout than this node is configured for ({policy}).", -1);
        }

        metadata.WriteStamp(policy);
    }

    private sealed class CapturedBlock(int accounts, int storages)
    {
        public readonly List<NodeChange> Accounts = new(accounts);
        public readonly List<NodeChange> Storages = new(storages);
    }

    private readonly record struct NodeChange(ValueHash256 Scope, TreePath Path, byte[] Rlp);
}
