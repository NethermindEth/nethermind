// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class StorageSubtreeReplayer(
    ISortedKeyValueStore storageHistory,
    HistoryRowFormat rowFormat,
    bool rlpWrapSlots,
    ILogManager logManager)
{
    public void Replay(
        in TreePath slotPrefix,
        StoragePartitionRows rows,
        IReadOnlyList<ClearRecord> clears,
        ulong from,
        ulong to,
        CommitmentEmitter? emitter,
        Func<ValueHash256, SeriesKey>? seriesKeyFor,
        SeriesWriter series,
        ContractRootCheck? check,
        CancellationToken token)
    {
        foreach (ClearRecord clear in clears)
        {
            if (clear.Block > from && clear.Block <= to) rows.ContractOf(clear.Identity);
        }

        Contract[] contracts = new Contract[rows.Identities.Count];
        for (int i = 0; i < contracts.Length; i++)
        {
            contracts[i] = new Contract(rows.Identities[i], slotPrefix, seriesKeyFor?.Invoke(rows.Identities[i]), series);
            contracts[i].Reset(emitter, logManager);
        }

        List<StreamedSlot> streams = [];
        Span<byte> flatKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        foreach ((int contractIndex, ValueHash256 slot) in rows.StreamedSlots)
        {
            Contract contract = contracts[contractIndex];
            HistoryRowScanner.WriteStorageFlatKey(flatKey, contract.Identity, slot);
            HistoryRowCursor cursor = new(storageHistory, rowFormat, flatKey, from, to);
            if (cursor.TryReadStart(out ulong writtenAt, out byte[] start) && start.Length > 0 && !HistoryRowScanner.KilledByClear(clears, contract.Identity, writtenAt, asOf: from))
            {
                contract.Tree!.Set(slot, start, rlpEncode: !rlpWrapSlots);
            }

            IEnumerator<(ulong Block, byte[] Value)> ascending = cursor.Ascending().GetEnumerator();
            streams.Add(new StreamedSlot(contractIndex, slot, ascending, ascending.MoveNext()));
        }

        foreach (StorageRowRef row in rows.Start)
        {
            contracts[row.Contract].Tree!.Set(row.Slot, rows.Arena.Slice(row.Offset, row.Length).ToArray(), rlpEncode: !rlpWrapSlots);
        }

        emitter?.BeginBlock(from);
        foreach (Contract contract in contracts)
        {
            contract.Recompute();
            contract.EmitView(from, emitter, force: true);
        }

        emitter?.CompleteBlock();

        rows.Deltas.Sort(static (a, b) => a.Block.CompareTo(b.Block));
        List<(ulong Block, int Contract)> clearEvents = [];
        foreach (ClearRecord clear in clears)
        {
            if (clear.Block > from && clear.Block <= to) clearEvents.Add((clear.Block, rows.ContractOf(clear.Identity)));
        }

        clearEvents.Sort(static (a, b) => a.Block.CompareTo(b.Block));

        HashSet<int> touched = [];
        int nextDelta = 0;
        int nextClear = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ulong block = ulong.MaxValue;
            if (nextDelta < rows.Deltas.Count) block = rows.Deltas[nextDelta].Block;
            if (nextClear < clearEvents.Count && clearEvents[nextClear].Block < block) block = clearEvents[nextClear].Block;
            foreach (StreamedSlot stream in streams)
            {
                if (stream.HasRow && stream.Rows.Current.Block < block) block = stream.Rows.Current.Block;
            }

            if (block == ulong.MaxValue) break;

            emitter?.BeginBlock(block);
            touched.Clear();
            while (nextClear < clearEvents.Count && clearEvents[nextClear].Block == block)
            {
                int contract = clearEvents[nextClear++].Contract;
                contracts[contract].Reset(emitter, logManager);
                touched.Add(contract);
            }

            while (nextDelta < rows.Deltas.Count && rows.Deltas[nextDelta].Block == block)
            {
                StorageRowRef row = rows.Deltas[nextDelta++];
                contracts[row.Contract].Tree!.Set(row.Slot, rows.Arena.Slice(row.Offset, row.Length).ToArray(), rlpEncode: !rlpWrapSlots);
                touched.Add(row.Contract);
            }

            foreach (StreamedSlot stream in streams)
            {
                if (!stream.HasRow || stream.Rows.Current.Block != block) continue;

                contracts[stream.Contract].Tree!.Set(stream.Slot, stream.Rows.Current.Value, rlpEncode: !rlpWrapSlots);
                touched.Add(stream.Contract);
                stream.HasRow = stream.Rows.MoveNext();
            }

            foreach (int index in touched)
            {
                Contract contract = contracts[index];
                contract.Recompute();
                contract.EmitView(block, emitter, force: false);
                contract.Events?.Add((block, contract.Tree!.RootHash.ValueHash256));
            }

            emitter?.CompleteBlock();
        }

        foreach (StreamedSlot stream in streams) stream.Rows.Dispose();

        if (check is null) return;

        foreach (Contract contract in contracts)
        {
            token.ThrowIfCancellationRequested();
            check.Begin(contract.Identity, from, to);
            foreach ((ulong block, ValueHash256 root) in contract.Events!) check.OnRoot(block, root);
            check.End();
        }
    }

    private sealed class StreamedSlot(int contract, ValueHash256 slot, IEnumerator<(ulong Block, byte[] Value)> rows, bool hasRow)
    {
        public readonly int Contract = contract;
        public readonly ValueHash256 Slot = slot;
        public readonly IEnumerator<(ulong Block, byte[] Value)> Rows = rows;
        public bool HasRow = hasRow;
    }

    private sealed class Contract(ValueHash256 identity, TreePath slotPrefix, SeriesKey? seriesKey, SeriesWriter series)
    {
        private ValueHash256 _lastRoot;
        private bool _haveRoot;
        private NodeViewKind _lastKind = NodeViewKind.Empty;
        private byte[]?[]? _lastChildren;
        private CommitmentRecordingTrieStore? _recording;
        private IScopedTrieStore? _store;

        public readonly ValueHash256 Identity = identity;
        public readonly List<(ulong Block, ValueHash256 Root)>? Events = seriesKey is null ? [] : null;

        public StorageTree? Tree { get; private set; }

        public void Reset(CommitmentEmitter? emitter, ILogManager logManager)
        {
            RawScopedTrieStore raw = new(new MemDb());
            _recording = emitter is null ? null : new CommitmentRecordingTrieStore(raw, emitter, Identity, slotPrefix.Length);
            _store = _recording ?? (IScopedTrieStore)raw;
            Tree = new StorageTree(_store, logManager);
        }

        public void Recompute()
        {
            if (_recording is null)
            {
                Tree!.UpdateRootHash();
                return;
            }

            Tree!.Commit();
        }

        public void EmitView(ulong block, CommitmentEmitter? emitter, bool force)
        {
            if (seriesKey is null) return;

            ValueHash256 root = Tree!.RootHash.ValueHash256;
            if (!force && _haveRoot && root == _lastRoot) return;

            _haveRoot = true;
            _lastRoot = root;
            NodeView view = NodeViews.FromRoot(Tree.RootRef, slotPrefix.Length, _store!);
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
                if (view.Kind == NodeViewKind.Empty) emitter.RecordStorageEmpty(Identity, slotPrefix);
                else if (view.Kind == NodeViewKind.Branch) emitter.RecordStorageNode(Identity, slotPrefix, view.Rlp!, changed);
                else emitter.RecordStorageNode(Identity, slotPrefix, view.Rlp!, changedChildren: 0);
            }

            _lastKind = view.Kind;
            _lastChildren = view.Children;
        }
    }
}
