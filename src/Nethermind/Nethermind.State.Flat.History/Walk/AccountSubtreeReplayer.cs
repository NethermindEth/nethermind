// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class AccountSubtreeReplayer(ISortedKeyValueStore accountHistory, HistoryRowFormat rowFormat, ILogManager logManager)
{
    public const ulong DefaultCheckpointBlocks = 4096;

    public void Replay(
        in TreePath prefix,
        AccountPartitionRows rows,
        ulong from,
        ulong to,
        CommitmentEmitter? emitter,
        SeriesKey seriesKey,
        SeriesWriter series,
        StorageRootMoveCheck moveCheck,
        WalkProgress progress,
        int item,
        ulong? resumeFrom,
        ulong checkpointBlocks,
        Action<ulong>? checkpoint,
        CancellationToken token)
    {
        long replayed = 0;
        ulong replayedUpTo = resumeFrom ?? from;
        RawScopedTrieStore store = new(new MemDb());
        StateTree state = new(store, logManager);
        TrieChangeCollector? changes = emitter is null ? null : new TrieChangeCollector();
        using SeriesPublisher publisher = new(SeriesScope.Accounts, prefix, seriesKey, series);

        List<StreamedAccount> streams = [];
        try
        {
            foreach (ValueHash256 path in rows.StreamedPaths)
            {
                HistoryRowCursor cursor = new(accountHistory, rowFormat, path.Bytes, replayedUpTo, to, token);
                ValueHash256 startRoot = Keccak.EmptyTreeHash.ValueHash256;
                if (cursor.TryReadStart(out _, out byte[] start) && start.Length > 0)
                {
                    state.Set(path, HistoryRowScanner.DecodeAccount(start));
                    startRoot = HistoryRowScanner.StorageRootOf(start);
                }

                streams.Add(new StreamedAccount(path, cursor, cursor.MoveNext(), startRoot));
            }

            foreach (AccountRowRef row in rows.Start)
            {
                state.Set(row.Path, HistoryRowScanner.DecodeAccount(rows.Arena.Slice(row.Offset, row.Length)));
            }

            rows.Deltas.Sort(static (a, b) => a.Block.CompareTo(b.Block));
            int next = 0;
            if (resumeFrom is { } resumed)
            {
                while (next < rows.Deltas.Count && rows.Deltas[next].Block <= resumed)
                {
                    AccountRowRef row = rows.Deltas[next++];
                    state.Set(row.Path, HistoryRowScanner.DecodeAccount(rows.Arena.Slice(row.Offset, row.Length)));
                }

                state.UpdateRootHash();
            }
            else
            {
                emitter?.BeginBlock(from);
                Recompute(state, changes, emitter, prefix.Length);
                Publish(publisher, from, state, prefix.Length, store, emitter);
                emitter?.CompleteBlock();
            }

            ulong lastCheckpoint = replayedUpTo;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ulong block = ulong.MaxValue;
                if (next < rows.Deltas.Count) block = rows.Deltas[next].Block;
                foreach (StreamedAccount stream in streams)
                {
                    if (stream.HasRow && stream.Rows.Block < block) block = stream.Rows.Block;
                }

                if (block == ulong.MaxValue) break;

                if ((++replayed & ((long)WalkProgress.BlocksPerUpdate - 1)) == 0)
                {
                    progress.AddReplayedBlocks((long)WalkProgress.BlocksPerUpdate);
                    progress.Replaying(item, block);
                }

                emitter?.BeginBlock(block);
                while (next < rows.Deltas.Count && rows.Deltas[next].Block == block)
                {
                    AccountRowRef row = rows.Deltas[next++];
                    state.Set(row.Path, HistoryRowScanner.DecodeAccount(rows.Arena.Slice(row.Offset, row.Length)));
                }

                foreach (StreamedAccount stream in streams)
                {
                    if (!stream.HasRow || stream.Rows.Block != block) continue;

                    ReadOnlySpan<byte> value = stream.Rows.Value;
                    state.Set(stream.Path, HistoryRowScanner.DecodeAccount(value));
                    ValueHash256 root = HistoryRowScanner.StorageRootOf(value);
                    if (root != stream.LastRoot) moveCheck.OnMoved(stream.Path, block, stream.LastRoot, root);
                    stream.LastRoot = root;
                    stream.HasRow = stream.Rows.MoveNext();
                }

                Recompute(state, changes, emitter, prefix.Length);
                if (publisher.IsNew(state.RootHash.ValueHash256)) Publish(publisher, block, state, prefix.Length, store, emitter);
                emitter?.CompleteBlock();

                if (checkpoint is not null && block - lastCheckpoint >= checkpointBlocks)
                {
                    emitter?.FlushOpenWindows();
                    series.Flush();
                    checkpoint(block);
                    lastCheckpoint = block;
                }
            }
        }
        finally
        {
            foreach (StreamedAccount stream in streams) stream.Rows.Dispose();
        }
    }

    private static void Publish(SeriesPublisher publisher, ulong block, StateTree state, int depth, ITrieNodeResolver resolver, CommitmentEmitter? emitter)
    {
        NodeView view = NodeViews.FromRoot(state.RootRef, depth, resolver);
        publisher.Publish(block, view, emitter);
        view.Release();
    }

    private static void Recompute(PatriciaTree tree, TrieChangeCollector? changes, CommitmentEmitter? emitter, int minRecordedDepth)
    {
        changes?.Collect(tree.RootRef);
        tree.UpdateRootHash();
        if (changes is not null) changes.RecordAccounts(emitter!, minRecordedDepth);
    }

    private sealed class StreamedAccount(ValueHash256 path, HistoryRowCursor rows, bool hasRow, ValueHash256 lastRoot)
    {
        public readonly ValueHash256 Path = path;
        public readonly HistoryRowCursor Rows = rows;
        public bool HasRow = hasRow;
        public ValueHash256 LastRoot = lastRoot;
    }
}
