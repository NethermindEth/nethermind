// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class AccountSubtreeReplayer(ISortedKeyValueStore accountHistory, HistoryRowFormat rowFormat, ILogManager logManager)
{
    public void Replay(
        in TreePath prefix,
        AccountPartitionRows rows,
        ulong from,
        ulong to,
        CommitmentEmitter? emitter,
        SeriesKey? seriesKey,
        SeriesWriter series,
        StorageRootMoveCheck moveCheck,
        CancellationToken token)
    {
        RawScopedTrieStore raw = new(new MemDb());
        CommitmentRecordingTrieStore? recording = emitter is null ? null : new CommitmentRecordingTrieStore(raw, emitter, storageAccount: null, prefix.Length);
        IScopedTrieStore store = recording ?? (IScopedTrieStore)raw;
        StateTree state = new(store, logManager);

        List<StreamedAccount> streams = [];
        foreach (ValueHash256 path in rows.StreamedPaths)
        {
            HistoryRowCursor cursor = new(accountHistory, rowFormat, path.Bytes, from, to);
            ValueHash256 startRoot = Keccak.EmptyTreeHash.ValueHash256;
            if (cursor.TryReadStart(out _, out byte[] start) && start.Length > 0)
            {
                state.Set(path, DecodeAccount(start));
                startRoot = ContractRootCheck.StorageRootOf(start);
            }

            IEnumerator<(ulong Block, byte[] Value)> ascending = cursor.Ascending().GetEnumerator();
            streams.Add(new StreamedAccount(path, ascending, ascending.MoveNext(), startRoot));
        }

        foreach (AccountRowRef row in rows.Start)
        {
            state.Set(row.Path, DecodeAccount(rows.Arena.Slice(row.Offset, row.Length)));
        }

        ViewEmitter view = new(prefix, seriesKey, series);
        emitter?.BeginBlock(from);
        Recompute(state, recording);
        view.Emit(from, state, store, emitter, force: true);
        emitter?.CompleteBlock();

        rows.Deltas.Sort(static (a, b) => a.Block.CompareTo(b.Block));
        int next = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ulong block = ulong.MaxValue;
            if (next < rows.Deltas.Count) block = rows.Deltas[next].Block;
            foreach (StreamedAccount stream in streams)
            {
                if (stream.HasRow && stream.Rows.Current.Block < block) block = stream.Rows.Current.Block;
            }

            if (block == ulong.MaxValue) break;

            emitter?.BeginBlock(block);
            while (next < rows.Deltas.Count && rows.Deltas[next].Block == block)
            {
                AccountRowRef row = rows.Deltas[next++];
                state.Set(row.Path, DecodeAccount(rows.Arena.Slice(row.Offset, row.Length)));
            }

            foreach (StreamedAccount stream in streams)
            {
                if (!stream.HasRow || stream.Rows.Current.Block != block) continue;

                byte[] value = stream.Rows.Current.Value;
                state.Set(stream.Path, DecodeAccount(value));
                ValueHash256 root = value.Length == 0 ? Keccak.EmptyTreeHash.ValueHash256 : ContractRootCheck.StorageRootOf(value);
                if (root != stream.LastRoot) moveCheck.OnMoved(stream.Path, block, stream.LastRoot, root);
                stream.LastRoot = root;
                stream.HasRow = stream.Rows.MoveNext();
            }

            Recompute(state, recording);
            view.Emit(block, state, store, emitter, force: false);
            emitter?.CompleteBlock();
        }

        foreach (StreamedAccount stream in streams) stream.Rows.Dispose();
    }

    private static void Recompute(PatriciaTree tree, CommitmentRecordingTrieStore? recording)
    {
        if (recording is null)
        {
            tree.UpdateRootHash();
            return;
        }

        tree.Commit();
    }

    public static Account? DecodeAccount(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return null;

        RlpReader reader = new(value);
        if (!AccountDecoder.Slim.TryDecodeStruct(ref reader, out AccountStruct account))
        {
            throw new InvalidOperationException("An account history row failed to decode; the column is corrupt.");
        }

        return new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment());
    }

    private sealed class StreamedAccount(ValueHash256 path, IEnumerator<(ulong Block, byte[] Value)> rows, bool hasRow, ValueHash256 lastRoot)
    {
        public readonly ValueHash256 Path = path;
        public readonly IEnumerator<(ulong Block, byte[] Value)> Rows = rows;
        public bool HasRow = hasRow;
        public ValueHash256 LastRoot = lastRoot;
    }

    private sealed class ViewEmitter(TreePath prefix, SeriesKey? seriesKey, SeriesWriter series)
    {
        private ValueHash256 _lastRoot = default;
        private bool _haveRoot;
        private NodeViewKind _lastKind = NodeViewKind.Empty;
        private byte[]?[]? _lastChildren;

        public void Emit(ulong block, StateTree state, ITrieNodeResolver resolver, CommitmentEmitter? emitter, bool force)
        {
            if (seriesKey is null) return;

            ValueHash256 root = state.RootHash.ValueHash256;
            if (!force && _haveRoot && root == _lastRoot) return;

            _haveRoot = true;
            _lastRoot = root;
            NodeView view = NodeViews.FromRoot(state.RootRef, prefix.Length, resolver);
            ushort changed = 0;
            if (view.Kind == NodeViewKind.Branch)
            {
                ushort presence = NodeViews.PresenceOf(view.Children!);
                changed = _lastKind == NodeViewKind.Branch ? NodeViews.ChangedChildren(_lastChildren, view.Children!) : presence;
                series.Write(seriesKey.Value, block, ParentRowCodec.EncodeBranch(block, presence, changed, view.Children!));
            }
            else if (view.Kind == NodeViewKind.Whole)
            {
                series.Write(seriesKey.Value, block, ParentRowCodec.EncodeWholeNode(block, view.Rlp!));
            }
            else
            {
                series.Write(seriesKey.Value, block, ParentRowCodec.EncodeEmpty(block));
            }

            if (emitter is not null)
            {
                if (view.Kind == NodeViewKind.Empty) emitter.RecordAccountEmpty(prefix);
                else if (view.Kind == NodeViewKind.Branch) emitter.RecordAccountNode(prefix, view.Rlp!, changed);
                else emitter.RecordAccountNode(prefix, view.Rlp!, changedChildren: 0);
            }

            _lastKind = view.Kind;
            _lastChildren = view.Children;
        }
    }
}
