// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Walk;
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
    private readonly CommitmentReclaimer _reclaimer;
    private readonly SortedDictionary<ulong, CapturedBlock> _buffered = [];
    private readonly Stack<CapturedBlock> _spare = new();
    private long _bufferedBytes;
    private ulong _firstBuffered;
    private ulong _lastBuffered;
    private bool _stopped;

    public ForwardCommitmentCapture(
        IColumnsDb<FlatHistoryColumns> history,
        CommitmentDepthPolicy policy,
        CommitmentMetadata metadata,
        ArchiveProofSettings settings,
        CommitmentReclaimer reclaimer,
        ILogManager logManager)
        : this(history, policy, metadata, settings, reclaimer, logManager, DefaultMaxBufferedBytes)
    {
    }

    internal ForwardCommitmentCapture(
        IColumnsDb<FlatHistoryColumns> history,
        CommitmentDepthPolicy policy,
        CommitmentMetadata metadata,
        ArchiveProofSettings settings,
        CommitmentReclaimer reclaimer,
        ILogManager logManager,
        long maxBufferedBytes)
    {
        _history = history;
        _policy = policy;
        _metadata = metadata;
        _settings = settings;
        _maxBufferedBytes = maxBufferedBytes;
        _logger = logManager.GetClassLogger<ForwardCommitmentCapture>();
        _reclaimer = reclaimer;
    }

    public bool Enabled => _settings.BuildEnabled;

    public void Capture(ulong block, Snapshot snapshot)
    {
        if (!Enabled || _stopped) return;

        try
        {
            CapturedBlock captured = Take();
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
            CapturedBlock captured = Take();
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
        foreach (CapturedBlock captured in _buffered.Values) Recycle(captured);
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

    private CapturedBlock Take() => _spare.TryPop(out CapturedBlock? spare) ? spare : new CapturedBlock();

    private void Recycle(CapturedBlock captured)
    {
        captured.Reset();
        _spare.Push(captured);
    }

    private void AddAccount(CapturedBlock captured, in TreePath path, ReadOnlySpan<byte> rlp)
    {
        if (rlp.Length < Hash256.Size || path.Length > _policy.AccountCheckpointDepth + 1) return;

        captured.Accounts.Add(new NodeChange(default, path, captured.Arena.Append(rlp), rlp.Length));
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

        captured.Storages.Add(new NodeChange(accountPath, path, captured.Arena.Append(rlp), rlp.Length));
        captured.Bytes += rlp.Length;
    }

    private void Buffer(ulong block, CapturedBlock captured)
    {
        if (_buffered.Count >= MaxBufferedBlocks || _bufferedBytes + captured.Bytes > _maxBufferedBytes)
        {
            Recycle(captured);
            Discard();
            _stopped = true;
            if (_logger.IsWarn) _logger.Warn(
                $"Archive proof commitment capture at the tip stopped: a single capture round spans more than {MaxBufferedBlocks} blocks or {_maxBufferedBytes} bytes of trie nodes. The retrofit walk covers that range instead.");
            return;
        }

        if (_buffered.Remove(block, out CapturedBlock? replaced))
        {
            _bufferedBytes -= replaced.Bytes;
            Recycle(replaced);
        }

        if (_buffered.Count == 0 || block < _firstBuffered) _firstBuffered = block;
        if (_buffered.Count == 0 || block > _lastBuffered) _lastBuffered = block;
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
        ulong first = _firstBuffered;
        ulong last = _lastBuffered;

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

                foreach (NodeChange change in captured.Accounts) emitter.RecordAccountNode(change.Path, captured.Arena.Slice(change.Offset, change.Length));
                foreach (NodeChange change in captured.Storages) emitter.RecordStorageNode(change.Scope, change.Path, captured.Arena.Slice(change.Offset, change.Length));
                emitter.CompleteBlock();
            }

            emitter.FlushOpenWindows();
        }

        _metadata.AdvanceTipSeries(first, last, out bool restarted);
        if (restarted && _logger.IsWarn) _logger.Warn(
            $"Archive proof commitments at the tip resume at block {first} after a gap; the series restarts there and the gap is left to the retrofit walk.");
        _reclaimer.PruneBelow(last);
    }

    private void EnsureStamp() => _metadata.EnsureLayout(_policy, _settings.DiscardMismatchedLayout, _logger);

    private sealed class CapturedBlock
    {
        public readonly RowArena Arena = new();
        public readonly ArrayPoolList<NodeChange> Accounts = new(64);
        public readonly ArrayPoolList<NodeChange> Storages = new(64);
        public Dictionary<ValueHash256, int>? StorageDepths;
        public long Bytes;

        public void Reset()
        {
            Arena.Clear();
            Accounts.Clear();
            Storages.Clear();
            StorageDepths?.Clear();
            Bytes = 0;
        }
    }

    private readonly record struct NodeChange(ValueHash256 Scope, TreePath Path, int Offset, int Length);
}
