// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class SubtreeCombiner(SeriesReader reader, long maxRowsPerPartition)
{
    private const int RootCursors = BranchRlp.ChildCount * BranchRlp.ChildCount;

    public void Combine(
        in SeriesScope scope,
        in TreePath parent,
        Func<int, SeriesKey> childKey,
        SeriesKey? own,
        ulong from,
        ulong to,
        CommitmentEmitter? emitter,
        SeriesWriter writer,
        ViewObserver? observer,
        CancellationToken token)
    {
        SeriesKey[] keys = new SeriesKey[BranchRlp.ChildCount];
        for (int index = 0; index < BranchRlp.ChildCount; index++) keys[index] = childKey(index);

        using SeriesPublisher publisher = new(scope, parent, own, writer);
        using ChildSeries children = new(reader, keys, from, to, RowsPerCursor(BranchRlp.ChildCount), token);
        NodeView current = children.Combine();
        emitter?.BeginBlock(from);
        publisher.Publish(from, current, emitter);
        emitter?.CompleteBlock();

        bool observing = observer is not null && (!observer.ObservesEveryBlock || observer.OnBlock(from, current));
        ulong observed = from;
        while (children.TryAdvance(out ulong block))
        {
            token.ThrowIfCancellationRequested();
            if (observing && observer!.ObservesEveryBlock)
            {
                for (ulong quiet = observed + 1; quiet < block && observing; quiet++) observing = observer.OnBlock(quiet, current);
            }

            NodeView previous = current;
            current = children.Combine();
            bool moved = previous.Hash != current.Hash;
            previous.Release();
            emitter?.BeginBlock(block);
            publisher.Publish(block, current, emitter);
            emitter?.CompleteBlock();
            if (observing)
            {
                if (observer!.ObservesEveryBlock) observing = observer.OnBlock(block, current);
                else if (moved) observer.OnChanged(block, current);
            }

            observed = block;
        }

        if (observing && observer!.ObservesEveryBlock)
        {
            for (ulong quiet = observed + 1; quiet <= to && observing; quiet++) observing = observer.OnBlock(quiet, current);
        }

        current.Release();
        foreach (SeriesKey key in keys)
        {
            if (key.Scratch) writer.Delete(key);
        }
    }

    public void CombineRoot(
        Func<int, int, SeriesKey> grandchildKey,
        ulong from,
        ulong to,
        CommitmentEmitter? emitter,
        SeriesWriter writer,
        RootHeaderCheck root,
        WalkProgress progress,
        CancellationToken token)
    {
        ChildSeries[] groups = new ChildSeries[BranchRlp.ChildCount];
        SeriesPublisher[] groupPublishers = new SeriesPublisher[BranchRlp.ChildCount];
        NodeView[] groupViews = new NodeView[BranchRlp.ChildCount];
        try
        {
            for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
            {
                SeriesKey[] keys = new SeriesKey[BranchRlp.ChildCount];
                for (int child = 0; child < BranchRlp.ChildCount; child++) keys[child] = grandchildKey(nibble, child);
                groups[nibble] = new ChildSeries(reader, keys, from, to, RowsPerCursor(RootCursors), token);
                groupPublishers[nibble] = new SeriesPublisher(SeriesScope.Accounts, TreePath.FromNibble([(byte)nibble]), key: null, writer);
                groupViews[nibble] = groups[nibble].Combine();
            }

            using SeriesPublisher rootPublisher = new(SeriesScope.Accounts, TreePath.Empty, key: null, writer);
            NodeView current = NodeViews.Combine(groupViews);
            emitter?.BeginBlock(from);
            for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++) groupPublishers[nibble].Publish(from, groupViews[nibble], emitter);
            rootPublisher.Publish(from, current, emitter);
            emitter?.CompleteBlock();

            bool observing = root.OnBlock(from, current);
            ulong observed = from;
            ulong nextEpochStart = emitter is null ? ulong.MaxValue : emitter.Policy.EpochStart(emitter.Policy.Epoch(from) + 1);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ulong block = ulong.MaxValue;
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
                {
                    ulong next = groups[nibble].NextBlock;
                    if (next < block) block = next;
                }

                while (nextEpochStart <= to && nextEpochStart < block)
                {
                    emitter!.BeginBlock(nextEpochStart);
                    for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++) groupPublishers[nibble].Publish(nextEpochStart, groupViews[nibble], emitter);
                    rootPublisher.Publish(nextEpochStart, current, emitter);
                    emitter.CompleteBlock();
                    nextEpochStart += emitter.Policy.EpochBlocks;
                }

                if (block == ulong.MaxValue) break;

                if ((block & (WalkProgress.BlocksPerUpdate - 1)) < (observed & (WalkProgress.BlocksPerUpdate - 1)) || block - observed >= WalkProgress.BlocksPerUpdate) progress.Folding(block);
                for (ulong quiet = observed + 1; quiet < block && observing; quiet++) observing = root.OnBlock(quiet, current);

                emitter?.BeginBlock(block);
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
                {
                    if (groups[nibble].NextBlock != block) continue;

                    groups[nibble].ApplyAt(block);
                    NodeView previousGroup = groupViews[nibble];
                    groupViews[nibble] = groups[nibble].Combine();
                    previousGroup.Release();
                    if (groupPublishers[nibble].IsNew(groupViews[nibble].Hash)) groupPublishers[nibble].Publish(block, groupViews[nibble], emitter);
                }

                NodeView previousRoot = current;
                current = NodeViews.Combine(groupViews);
                previousRoot.Release();
                if (rootPublisher.IsNew(current.Hash)) rootPublisher.Publish(block, current, emitter);
                emitter?.CompleteBlock();

                if (observing) observing = root.OnBlock(block, current);
                observed = block;
            }

            for (ulong quiet = observed + 1; quiet <= to && observing; quiet++) observing = root.OnBlock(quiet, current);
            current.Release();
        }
        finally
        {
            foreach (ChildSeries? group in groups) group?.Dispose();
            foreach (SeriesPublisher? publisher in groupPublishers) publisher?.Dispose();
            foreach (NodeView view in groupViews) view.Release();
        }

        for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
        {
            for (int child = 0; child < BranchRlp.ChildCount; child++)
            {
                SeriesKey key = grandchildKey(nibble, child);
                if (key.Scratch) writer.Delete(key);
            }
        }
    }

    private int RowsPerCursor(int cursors) => (int)Math.Clamp(maxRowsPerPartition / cursors, SeriesReader.SeriesCursor.MinRowsBuffered, int.MaxValue);

    private sealed class ChildSeries : IDisposable
    {
        private readonly NodeSeriesState[] _states = new NodeSeriesState[BranchRlp.ChildCount];
        private readonly SeriesReader.SeriesCursor[] _cursors = new SeriesReader.SeriesCursor[BranchRlp.ChildCount];
        private readonly bool[] _hasRow = new bool[BranchRlp.ChildCount];
        private readonly NodeView[] _views = new NodeView[BranchRlp.ChildCount];

        public ChildSeries(SeriesReader reader, SeriesKey[] keys, ulong from, ulong to, int rowsPerCursor, CancellationToken token)
        {
            try
            {
                for (int index = 0; index < BranchRlp.ChildCount; index++)
                {
                    _states[index] = reader.ReadStart(keys[index], from);
                    _views[index] = _states[index].ToView();
                    _cursors[index] = reader.Open(keys[index], from, to, rowsPerCursor, token);
                    _hasRow[index] = _cursors[index].MoveNext();
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public ulong NextBlock
        {
            get
            {
                ulong block = ulong.MaxValue;
                for (int index = 0; index < BranchRlp.ChildCount; index++)
                {
                    if (_hasRow[index] && _cursors[index].Block < block) block = _cursors[index].Block;
                }

                return block;
            }
        }

        public bool TryAdvance(out ulong block)
        {
            block = NextBlock;
            if (block == ulong.MaxValue) return false;

            ApplyAt(block);
            return true;
        }

        public void ApplyAt(ulong block)
        {
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                if (!_hasRow[index] || _cursors[index].Block != block) continue;

                _states[index].Apply(_cursors[index].Row);
                NodeView previous = _views[index];
                _views[index] = _states[index].ToView();
                previous.Release();
                _hasRow[index] = _cursors[index].MoveNext();
            }
        }

        public NodeView Combine() => NodeViews.Combine(_views);

        public void Dispose()
        {
            foreach (SeriesReader.SeriesCursor cursor in _cursors) cursor?.Dispose();
            foreach (NodeSeriesState state in _states) state?.Dispose();
            foreach (NodeView view in _views) view.Release();
        }
    }
}
