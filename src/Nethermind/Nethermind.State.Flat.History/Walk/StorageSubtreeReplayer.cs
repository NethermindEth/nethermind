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
    ISortedKeyValueStore accountHistory,
    ISortedKeyValueStore storageHistory,
    HistoryRowFormat rowFormat,
    bool rlpWrapSlots,
    ILogManager logManager,
    MismatchSink sink)
{
    public void Replay(
        in TreePath slotPrefix,
        StoragePartitionRows rows,
        IReadOnlyList<ClearRecord> clears,
        ulong from,
        ulong to,
        CommitmentEmitter? emitter,
        SeriesWriter series,
        bool writeSeries,
        CancellationToken token)
    {
        Contract[] contracts = new Contract[rows.Identities.Count];
        for (int i = 0; i < contracts.Length; i++)
        {
            ValueHash256 identity = rows.Identities[i];
            SeriesPublisher? publisher = writeSeries ? new SeriesPublisher(SeriesScope.Storage(identity), slotPrefix, SeriesScope.Storage(identity).Key(slotPrefix, scratch: true), series) : null;
            ContractRootCheck? check = null;
            if (!writeSeries)
            {
                check = new ContractRootCheck(accountHistory, rowFormat, sink);
                check.Begin(identity, from, to, token);
            }

            contracts[i] = new Contract(identity, slotPrefix, publisher, check);
            contracts[i].Reset(emitter, logManager);
        }

        List<StreamedSlot> streams = [];
        Span<byte> flatKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        foreach ((int contractIndex, ValueHash256 slot) in rows.StreamedSlots)
        {
            Contract contract = contracts[contractIndex];
            HistoryRowScanner.WriteStorageFlatKey(flatKey, contract.Identity, slot);
            HistoryRowCursor cursor = new(storageHistory, rowFormat, flatKey, from, to, token);
            if (cursor.TryReadStart(out ulong writtenAt, out byte[] start) && start.Length > 0 && !HistoryRowScanner.KilledByClear(clears, contract.Identity, writtenAt, asOf: from))
            {
                contract.Tree!.Set(slot, start, rlpEncode: !rlpWrapSlots);
            }

            streams.Add(new StreamedSlot(contractIndex, slot, cursor, cursor.MoveNext()));
        }

        foreach (StorageRowRef row in rows.Start)
        {
            contracts[row.Contract].Tree!.Set(row.Slot, rows.Arena.Slice(row.Offset, row.Length).ToArray(), rlpEncode: !rlpWrapSlots);
        }

        emitter?.BeginBlock(from);
        foreach (Contract contract in contracts)
        {
            contract.Recompute();
            contract.PublishAnchor(from, emitter);
        }

        emitter?.CompleteBlock();

        rows.Deltas.Sort(static (a, b) => a.Block.CompareTo(b.Block));
        List<(ulong Block, int Contract)> clearEvents = [];
        foreach (ClearRecord clear in clears)
        {
            if (clear.Block > from && clear.Block <= to && rows.TryGetContract(clear.Identity, out int contract)) clearEvents.Add((clear.Block, contract));
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
                if (stream.HasRow && stream.Rows.Block < block) block = stream.Rows.Block;
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
                if (!stream.HasRow || stream.Rows.Block != block) continue;

                contracts[stream.Contract].Tree!.Set(stream.Slot, stream.Rows.Value.ToArray(), rlpEncode: !rlpWrapSlots);
                touched.Add(stream.Contract);
                stream.HasRow = stream.Rows.MoveNext();
            }

            foreach (int index in touched)
            {
                Contract contract = contracts[index];
                contract.Recompute();
                contract.PublishChange(block, emitter);
            }

            emitter?.CompleteBlock();
        }

        foreach (Contract contract in contracts) contract.Check?.End();
    }

    private sealed class StreamedSlot(int contract, ValueHash256 slot, HistoryRowCursor rows, bool hasRow)
    {
        public readonly int Contract = contract;
        public readonly ValueHash256 Slot = slot;
        public readonly HistoryRowCursor Rows = rows;
        public bool HasRow = hasRow;
    }

    private sealed class Contract(ValueHash256 identity, TreePath slotPrefix, SeriesPublisher? publisher, ContractRootCheck? check)
    {
        private readonly TrieChangeCollector _changes = new();
        private IScopedTrieStore? _store;
        private CommitmentEmitter? _emitter;

        public readonly ValueHash256 Identity = identity;

        public ContractRootCheck? Check => check;

        public StorageTree? Tree { get; private set; }

        public void Reset(CommitmentEmitter? emitter, ILogManager logManager)
        {
            _emitter = emitter;
            _store = new RawScopedTrieStore(new MemDb());
            Tree = new StorageTree(_store, logManager);
        }

        public void Recompute()
        {
            if (_emitter is not null) _changes.Collect(Tree!.RootRef);
            Tree!.UpdateRootHash();
            if (_emitter is not null) _changes.RecordStorage(_emitter, Identity, slotPrefix.Length);
        }

        public void PublishAnchor(ulong block, CommitmentEmitter? emitter) =>
            publisher?.Publish(block, NodeViews.FromRoot(Tree!.RootRef, slotPrefix.Length, _store!), emitter);

        public void PublishChange(ulong block, CommitmentEmitter? emitter)
        {
            ValueHash256 root = Tree!.RootHash.ValueHash256;
            check?.OnRoot(block, root);
            if (publisher is not null && publisher.IsNew(root)) publisher.Publish(block, NodeViews.FromRoot(Tree.RootRef, slotPrefix.Length, _store!), emitter);
        }
    }
}
