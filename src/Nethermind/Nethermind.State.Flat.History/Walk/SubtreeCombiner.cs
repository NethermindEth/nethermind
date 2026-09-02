// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class SubtreeCombiner(SeriesReader reader)
{
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

        SeriesPublisher publisher = new(scope, parent, own, writer);
        ChildSeries children = new(reader, keys, from, to, token);
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

            current = children.Combine();
            emitter?.BeginBlock(block);
            publisher.Publish(block, current, emitter);
            emitter?.CompleteBlock();
            if (observing)
            {
                if (observer!.ObservesEveryBlock) observing = observer.OnBlock(block, current);
                else observer.OnChanged(block, current);
            }

            observed = block;
        }

        if (observing && observer!.ObservesEveryBlock)
        {
            for (ulong quiet = observed + 1; quiet <= to && observing; quiet++) observing = observer.OnBlock(quiet, current);
        }

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
        CancellationToken token)
    {
        ChildSeries[] groups = new ChildSeries[BranchRlp.ChildCount];
        SeriesPublisher[] groupPublishers = new SeriesPublisher[BranchRlp.ChildCount];
        NodeView[] groupViews = new NodeView[BranchRlp.ChildCount];
        {
            for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
            {
                SeriesKey[] keys = new SeriesKey[BranchRlp.ChildCount];
                for (int child = 0; child < BranchRlp.ChildCount; child++) keys[child] = grandchildKey(nibble, child);
                groups[nibble] = new ChildSeries(reader, keys, from, to, token);
                groupPublishers[nibble] = new SeriesPublisher(SeriesScope.Accounts, TreePath.FromNibble([(byte)nibble]), key: null, writer);
                groupViews[nibble] = groups[nibble].Combine();
            }

            SeriesPublisher rootPublisher = new(SeriesScope.Accounts, TreePath.Empty, key: null, writer);
            NodeView current = NodeViews.Combine(groupViews);
            emitter?.BeginBlock(from);
            for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++) groupPublishers[nibble].Publish(from, groupViews[nibble], emitter);
            rootPublisher.Publish(from, current, emitter);
            emitter?.CompleteBlock();

            bool observing = root.OnBlock(from, current);
            ulong observed = from;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ulong block = ulong.MaxValue;
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
                {
                    ulong next = groups[nibble].NextBlock;
                    if (next < block) block = next;
                }

                if (block == ulong.MaxValue) break;

                for (ulong quiet = observed + 1; quiet < block && observing; quiet++) observing = root.OnBlock(quiet, current);

                emitter?.BeginBlock(block);
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
                {
                    if (groups[nibble].NextBlock != block) continue;

                    groups[nibble].ApplyAt(block);
                    groupViews[nibble] = groups[nibble].Combine();
                    if (groupPublishers[nibble].IsNew(groupViews[nibble].Hash)) groupPublishers[nibble].Publish(block, groupViews[nibble], emitter);
                }

                current = NodeViews.Combine(groupViews);
                if (rootPublisher.IsNew(current.Hash)) rootPublisher.Publish(block, current, emitter);
                emitter?.CompleteBlock();

                if (observing) observing = root.OnBlock(block, current);
                observed = block;
            }

            for (ulong quiet = observed + 1; quiet <= to && observing; quiet++) observing = root.OnBlock(quiet, current);
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

    private sealed class ChildSeries
    {
        private readonly NodeSeriesState[] _states = new NodeSeriesState[BranchRlp.ChildCount];
        private readonly SeriesReader.SeriesCursor[] _cursors = new SeriesReader.SeriesCursor[BranchRlp.ChildCount];
        private readonly bool[] _hasRow = new bool[BranchRlp.ChildCount];
        private readonly NodeView[] _views = new NodeView[BranchRlp.ChildCount];

        public ChildSeries(SeriesReader reader, SeriesKey[] keys, ulong from, ulong to, CancellationToken token)
        {
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                _states[index] = reader.ReadStart(keys[index], from);
                _views[index] = _states[index].ToView();
                _cursors[index] = reader.Open(keys[index], from, to, token);
                _hasRow[index] = _cursors[index].MoveNext();
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
                _views[index] = _states[index].ToView();
                _hasRow[index] = _cursors[index].MoveNext();
            }
        }

        public NodeView Combine() => NodeViews.Combine(_views);

    }
}
