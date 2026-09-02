// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class SubtreeCombiner(SeriesReader reader)
{
    public void Combine(
        bool isStorage,
        in ValueHash256 scope,
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
        NodeSeriesState[] states = new NodeSeriesState[BranchRlp.ChildCount];
        IEnumerator<(ulong Block, byte[] Row)>[] cursors = new IEnumerator<(ulong Block, byte[] Row)>[BranchRlp.ChildCount];
        bool[] hasRow = new bool[BranchRlp.ChildCount];
        NodeView[] views = new NodeView[BranchRlp.ChildCount];
        SeriesKey[] keys = new SeriesKey[BranchRlp.ChildCount];

        try
        {
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                keys[index] = childKey(index);
                states[index] = reader.ReadStart(keys[index], from);
                views[index] = states[index].ToView();
                cursors[index] = reader.ReadAscending(keys[index], from, to).GetEnumerator();
                hasRow[index] = cursors[index].MoveNext();
            }

            NodeView current = NodeViews.Combine(views);
            NodeViewKind previousKind = NodeViewKind.Empty;
            byte[]?[]? previousChildren = null;
            Emit(from, current, ref previousKind, ref previousChildren, isStorage, scope, parent, own, emitter, writer);

            bool everyBlock = observer?.ObservesEveryBlock ?? false;
            bool observing = observer is not null;
            if (everyBlock) observing = observer!.OnBlock(from, current);

            ulong observed = from;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ulong block = ulong.MaxValue;
                for (int index = 0; index < BranchRlp.ChildCount; index++)
                {
                    if (hasRow[index] && cursors[index].Current.Block < block) block = cursors[index].Current.Block;
                }

                if (block == ulong.MaxValue) break;

                if (everyBlock && observing)
                {
                    for (ulong quiet = observed + 1; quiet < block && observing; quiet++) observing = observer!.OnBlock(quiet, current);
                }

                for (int index = 0; index < BranchRlp.ChildCount; index++)
                {
                    if (!hasRow[index] || cursors[index].Current.Block != block) continue;

                    states[index].Apply(cursors[index].Current.Row);
                    views[index] = states[index].ToView();
                    hasRow[index] = cursors[index].MoveNext();
                }

                current = NodeViews.Combine(views);
                Emit(block, current, ref previousKind, ref previousChildren, isStorage, scope, parent, own, emitter, writer);
                if (observing)
                {
                    if (everyBlock) observing = observer!.OnBlock(block, current);
                    else observer!.OnChanged(block, current);
                }

                observed = block;
            }

            if (everyBlock && observing)
            {
                for (ulong quiet = observed + 1; quiet <= to && observing; quiet++) observing = observer!.OnBlock(quiet, current);
            }
        }
        finally
        {
            for (int index = 0; index < BranchRlp.ChildCount; index++) cursors[index]?.Dispose();
        }

        for (int index = 0; index < BranchRlp.ChildCount; index++) writer.Delete(keys[index]);
    }

    private void Emit(
        ulong block,
        in NodeView view,
        ref NodeViewKind previousKind,
        ref byte[]?[]? previousChildren,
        bool isStorage,
        in ValueHash256 scope,
        in TreePath parent,
        SeriesKey? own,
        CommitmentEmitter? emitter,
        SeriesWriter writer)
    {
        ushort changed = 0;
        if (view.Kind == NodeViewKind.Branch)
        {
            ushort presence = NodeViews.PresenceOf(view.Children!);
            changed = previousKind == NodeViewKind.Branch ? NodeViews.ChangedChildren(previousChildren, view.Children!) : presence;
            if (own is { } branchKey) writer.Write(branchKey, block, ParentRowCodec.EncodeBranch(block, presence, changed, view.Children!));
        }
        else if (own is { } key)
        {
            writer.Write(key, block, view.Kind == NodeViewKind.Whole ? ParentRowCodec.EncodeWholeNode(block, view.Rlp!) : ParentRowCodec.EncodeEmpty(block));
        }

        if (emitter is not null)
        {
            emitter.BeginBlock(block);
            if (isStorage)
            {
                if (view.Kind == NodeViewKind.Empty) emitter.RecordStorageEmpty(scope, parent);
                else emitter.RecordStorageNode(scope, parent, view.Rlp!, changed);
            }
            else
            {
                if (view.Kind == NodeViewKind.Empty) emitter.RecordAccountEmpty(parent);
                else emitter.RecordAccountNode(parent, view.Rlp!, changed);
            }

            emitter.CompleteBlock();
        }

        previousKind = view.Kind;
        previousChildren = view.Children;
    }
}
