// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class ForwardCommitmentCapture
{
    public const int MaxBufferedBlocks = 4096;
    public const long DefaultMaxBufferedBytes = 256L * 1024 * 1024;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly CommitmentDepthPolicy _policy;
    private readonly CommitmentMetadata _metadata;
    private readonly ArchiveProofSettings _settings;
    private readonly long _maxBufferedBytes;
    private readonly ILogger _logger;
    private readonly SortedDictionary<ulong, CapturedBlock> _buffered = [];
    private long _bufferedBytes;
    private bool _stopped;

    public ForwardCommitmentCapture(
        IColumnsDb<FlatHistoryColumns> history,
        CommitmentDepthPolicy policy,
        CommitmentMetadata metadata,
        ArchiveProofSettings settings,
        ILogManager logManager)
        : this(history, policy, metadata, settings, logManager, DefaultMaxBufferedBytes)
    {
    }

    internal ForwardCommitmentCapture(
        IColumnsDb<FlatHistoryColumns> history,
        CommitmentDepthPolicy policy,
        CommitmentMetadata metadata,
        ArchiveProofSettings settings,
        ILogManager logManager,
        long maxBufferedBytes)
    {
        _history = history;
        _policy = policy;
        _metadata = metadata;
        _settings = settings;
        _maxBufferedBytes = maxBufferedBytes;
        _logger = logManager.GetClassLogger<ForwardCommitmentCapture>();
    }

    public bool Enabled => _settings.BuildEnabled;

    public void Capture(ulong block, Snapshot snapshot)
    {
        if (!Enabled || _stopped) return;

        try
        {
            CapturedBlock captured = new();
            foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> entry in snapshot.StateNodes)
            {
                AddAccount(captured, entry.Key.Key, entry.Value.FullRlp.AsSpan());
            }

            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> entry in snapshot.StorageNodes)
            {
                (Hash256 accountPath, TreePath path) = entry.Key.Key;
                AddStorage(captured, new ValueHash256(accountPath.Bytes), path, entry.Value.FullRlp.AsSpan());
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
            CapturedBlock captured = new();
            foreach (WholeReadScanner.StateNodeEntry entry in scanner.StateNodes)
            {
                AddAccount(captured, entry.Path, entry.Rlp);
            }

            foreach (WholeReadScanner.StorageNodeEntry entry in scanner.StorageNodes)
            {
                AddStorage(captured, entry.AddressHash, entry.Path, entry.Rlp);
            }

            Buffer(block, captured);
        }
        catch (Exception e)
        {
            Stop(e);
        }
    }

    public void Discard()
    {
        _buffered.Clear();
        _bufferedBytes = 0;
    }

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
            Discard();
        }
    }

    private void AddAccount(CapturedBlock captured, in TreePath path, ReadOnlySpan<byte> rlp)
    {
        if (rlp.Length < Hash256.Size || path.Length > _policy.AccountCheckpointDepth + 1) return;

        captured.Accounts.Add(new NodeChange(default, path, rlp.ToArray()));
        captured.Bytes += rlp.Length;
    }

    private void AddStorage(CapturedBlock captured, in ValueHash256 accountPath, in TreePath path, ReadOnlySpan<byte> rlp)
    {
        if (path.Length >= _policy.StorageRowsSignalDepth)
        {
            Dictionary<ValueHash256, int> depths = captured.StorageDepths ??= [];
            depths[accountPath] = Math.Max(depths.GetValueOrDefault(accountPath), path.Length);
        }

        if (rlp.Length < Hash256.Size || path.Length > _policy.StorageCheckpointDepth + 1) return;

        captured.Storages.Add(new NodeChange(accountPath, path, rlp.ToArray()));
        captured.Bytes += rlp.Length;
    }

    private void Buffer(ulong block, CapturedBlock captured)
    {
        if (_buffered.Count >= MaxBufferedBlocks || _bufferedBytes + captured.Bytes > _maxBufferedBytes)
        {
            Discard();
            _stopped = true;
            if (_logger.IsWarn) _logger.Warn(
                $"Archive proof commitment capture at the tip stopped: a single capture round spans more than {MaxBufferedBlocks} blocks or {_maxBufferedBytes} bytes of trie nodes. The retrofit walk covers that range instead.");
            return;
        }

        _buffered[block] = captured;
        _bufferedBytes += captured.Bytes;
    }

    private void Stop(Exception e)
    {
        _stopped = true;
        Discard();
        if (_logger.IsError) _logger.Error("Archive proof commitment capture at the tip failed and is stopped until restart; the retrofit walk can rebuild the missing range.", e);
    }

    private void Replay()
    {
        ulong first = _buffered.Keys.First();
        ulong last = _buffered.Keys.Last();

        EnsureStamp();
        using (CommitmentEmitter emitter = CommitmentEmitter.ForTip(_history, _policy, _metadata))
        {
            foreach ((ulong block, CapturedBlock captured) in _buffered)
            {
                emitter.BeginBlock(block);
                if (captured.StorageDepths is { } depths)
                {
                    foreach ((ValueHash256 account, int depth) in depths) emitter.RecordStorageDepthReached(account, depth);
                }

                foreach (NodeChange change in captured.Accounts) emitter.RecordAccountNode(change.Path, change.Rlp);
                foreach (NodeChange change in captured.Storages) emitter.RecordStorageNode(change.Scope, change.Path, change.Rlp);
                emitter.CompleteBlock();
            }
        }

        _metadata.AdvanceTipSeries(first, last, out bool restarted);
        if (restarted && _logger.IsWarn) _logger.Warn(
            $"Archive proof commitments at the tip resume at block {first} after a gap; the series restarts there and the gap is left to the retrofit walk.");
    }

    private void EnsureStamp() => _metadata.EnsureLayout(_policy, _settings.DiscardMismatchedLayout, _logger);

    private sealed class CapturedBlock
    {
        public readonly List<NodeChange> Accounts = [];
        public readonly List<NodeChange> Storages = [];
        public Dictionary<ValueHash256, int>? StorageDepths;
        public long Bytes;
    }

    private readonly record struct NodeChange(ValueHash256 Scope, TreePath Path, byte[] Rlp);
}
