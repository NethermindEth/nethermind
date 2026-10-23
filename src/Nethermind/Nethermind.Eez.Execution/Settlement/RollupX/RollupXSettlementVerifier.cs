// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal static class RollupXSettlementVerifier
{
    private static readonly UInt256 MaxOutboundValue = UInt256.One << 191;
    private static readonly UInt256 MaxInboundValue = MaxOutboundValue - 1;
    public static RollupXObservationClaim[] Verify(PostBatch batch, NativeStream stream,
        IReadOnlyList<EezStatelessBlockResult> window, ulong rollupId, CancellationToken cancellation = default)
    {
        RollupXOperationsCheck.Verify(OperationsPayload.Decode(stream.Operations), window, cancellation);
        RollupUpdate[] updates = RollupUpdateChain.Verify(batch, rollupId, window[0].Block.ParentHash!.ValueHash256, window[^1].Hash.ValueHash256);
        EezStatelessBlockResult terminal = window[^1];
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(terminal.Block);
        Verification verification = new(batch, updates, terminal, plan, rollupId, cancellation);
        bool inbound = false;
        int index = 0;
        while (index < stream.Messages.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            if (stream.Messages[index++] is not StreamMessage.Initiate start || start.ChainId != 0 && start.ChainId != rollupId)
                throw Invalid("Native transaction has an invalid origin.");
            if (start.ChainId == rollupId && inbound) throw Invalid("Outbound native transaction follows inbound execution.");
            inbound |= start.ChainId == 0;
            if (start.ChainId == 0 && !start.TxData.IsEmpty) throw Invalid("L1-origin transaction publishes unauthenticated transaction bytes.");
            NativeCallTree tree = NativeCallTree.Read(stream.Messages, ref index, start.ChainId, rollupId);
            verification.VerifyTransaction(start, tree);
        }
        return verification.Complete();
    }

    private static EezSettlementException Invalid(string message) => new(message);

    private sealed class Verification(PostBatch batch, RollupUpdate[] updates, EezStatelessBlockResult terminal,
        RollupXCheckpointPlan plan, ulong rollupId, CancellationToken cancellation)
    {
        private readonly List<RollupXObservationClaim> _observations = [];
        private readonly List<(int Transaction, ValueHash256 Hash)> _outboundEvents = [];
        private readonly NativeL1Tables _l1 = new(updates.AsMemory(1), rollupId, terminal.Checkpoints[0].BlockHash.ValueHash256);
        private int _transaction = plan.OrdinaryTransactions;
        private int _checkpoint = 1;
        private int _entry = 1;
        private int _staticEntry;
        private int _outbound;
        private ValueHash256 _liveRoot = terminal.Checkpoints[0].BlockHash.ValueHash256;

        public void VerifyTransaction(StreamMessage.Initiate start, NativeCallTree tree)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (NativeCallTree.Node call in tree.Calls)
            {
                if (call.Encoded.SourceRollupId != rollupId) continue;
                if (call.Encoded.SourceAddress == EezConstants.SystemAddress)
                    throw Invalid("Native call uses the reserved L2 system source.");
                if (start.ChainId != 0 && call.Encoded.Value > MaxOutboundValue)
                    throw Invalid("Outbound recorded call value exceeds the int192 outflow range.");
            }
            NativeExecutionTables tables = new(tree, rollupId, _l1);
            if (start.ChainId != 0) VerifyOutbound(start, tree, tables.Outbound());
            else VerifyInbound(tree, tables.Inbound());
        }

        private void VerifyOutbound(StreamMessage.Initiate start, NativeCallTree tree, ExecutionTable source)
        {
            if (_entry >= batch.Entries.Length || _transaction + 1 >= terminal.Block.Transactions.Length)
                throw Invalid("Outbound transaction has no matching settlement entry and load/user pair.");
            Transaction load = terminal.Block.Transactions[_transaction];
            Transaction user = terminal.Block.Transactions[_transaction + 1];
            if (!load.Value.IsZero) throw Invalid("Outbound load must carry zero native value.");
            if (user.IsEezSystemTransaction() || user.SenderAddress == EezConstants.SystemAddress ||
                !TxDecoder.Instance.Encode(user, RlpBehaviors.SkipTypedWrapping).Bytes.AsSpan().SequenceEqual(start.TxData.Span))
                throw Invalid("Native outbound transaction bytes differ from canonical execution.");
            ExecutionEntry expected = _l1.Entries[_entry - 1];
            VerifySystem(load, _transaction, NativeValue.Funding(source.Entries), EezCalldata.EncodeLoadExecutionTable(source));
            Hash256 checkpoint = NextCheckpoint(_transaction + 1);
            RollupUpdate update = updates[_entry];
            if (update.EtherDelta != NativeValue.Delta(tree) || update.CurrentRoot != _liveRoot || update.NewRoot != checkpoint.ValueHash256)
                throw Invalid("Outbound value movement or root update differs from canonical execution.");
            AddEvents(_transaction + 1);
            MatchEntry(expected);
            _liveRoot = update.NewRoot;
            _transaction += 2;
            _outbound++;
        }

        private void VerifyInbound(NativeCallTree tree, L2ExecutionEntry[] deliveries)
        {
            int delivery = 0;
            int revertedUntil = 0;
            foreach (int root in tree.Roots)
            {
                cancellation.ThrowIfCancellationRequested();
                NativeCallTree.Node call = tree.Calls[root];
                revertedUntil = Math.Max(revertedUntil, call.RevertEnd);
                if (call.Encoded.IsStatic)
                {
                    VerifyStatic(tree, call);
                    continue;
                }
                if (_entry >= batch.Entries.Length) throw Invalid("Native inbound call has no settlement entry.");
                ExecutionEntry expected = _l1.Entries[_entry - 1];
                RollupUpdate update = updates[_entry];
                if (update.CurrentRoot != _liveRoot) throw Invalid("Inbound call starts from a different FIFO root.");
                if (root >= revertedUntil && call.Result.Success)
                {
                    if (delivery == deliveries.Length || _transaction == terminal.Block.Transactions.Length)
                        throw Invalid("Successful inbound call has no canonical delivery.");
                    if (call.Encoded.Value > MaxInboundValue) throw Invalid("Inbound root value exceeds the positive int192 range.");
                    L2ExecutionEntry[] incoming = [deliveries[delivery++]];
                    VerifySystem(terminal.Block.Transactions[_transaction], _transaction, NativeValue.Funding(incoming),
                        EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall(incoming, [])));
                    if (update.NewRoot != NextCheckpoint(_transaction).ValueHash256 || update.EtherDelta != NativeValue.Delta(tree, call))
                        throw Invalid("Inbound value movement or root update differs from canonical execution.");
                    AddEvents(_transaction++);
                }
                else
                {
                    if (update.NewRoot != _liveRoot || !update.EtherDelta.IsZero || _l1.EntryEvents[_entry - 1].Length != 0)
                        throw Invalid("Inbound lookup changes the canonical commitment, value or outbound events.");
                    Observe(tree, call, ObservationMode.Lookup);
                }
                MatchEntry(expected);
                _liveRoot = update.NewRoot;
            }
            if (delivery != deliveries.Length) throw Invalid("Native stream has unconsumed inbound deliveries.");
        }

        private void VerifyStatic(NativeCallTree tree, NativeCallTree.Node call)
        {
            if (_staticEntry == batch.StaticEntries.Length || _staticEntry == _l1.StaticEntries.Count)
                throw Invalid("Native static call has no settlement entry.");
            StaticExecutionEntry expected = _l1.StaticEntries[_staticEntry];
            if (expected.ExpectedRoots is not [{ } pin] || pin.Root != _liveRoot ||
                EezCalldata.StaticEntryHash(expected) != EezCalldata.StaticEntryHash(batch.StaticEntries[_staticEntry++]))
                throw Invalid("Native static call differs from its FIFO settlement entry.");
            Observe(tree, call, ObservationMode.Static);
        }

        private void Observe(NativeCallTree tree, NativeCallTree.Node call, ObservationMode mode)
        {
            if (call.RevertEnd > call.Index + 1)
                throw Invalid("Observation begins a multi-action rollback region without transient checkpoints.");
            RollupXObservationCallback[] callbacks = new RollupXObservationCallback[call.Children.Count];
            for (int i = 0; i < callbacks.Length; i++)
            {
                NativeCallTree.Node child = tree.Calls[call.Children[i]];
                if (mode != ObservationMode.Static || !child.Encoded.IsStatic || !child.Result.Success || child.Children.Count != 0)
                    throw Invalid("Observation callback is outside the static source-chain profile.");
                callbacks[i] = new(child.Encoded.SourceAddress, child.Encoded.TargetAddress,
                    child.Encoded.Data.AsSpan().ToArray(), child.Result.ReturnData.ToArray());
            }
            _observations.Add(new RollupXObservationClaim(new Hash256(_liveRoot.Bytes), call.Encoded.SourceAddress,
                call.Encoded.SourceRollupId, call.Encoded.TargetAddress, call.Encoded.Data, call.Encoded.Value, mode,
                new ObservationOutcome(call.Result.Success, call.Result.ReturnData.ToArray()))
            { Callbacks = callbacks });
        }

        private void AddEvents(int transaction)
        {
            foreach (ValueHash256 hash in _l1.EntryEvents[_entry - 1]) _outboundEvents.Add((transaction, hash));
        }

        private void MatchEntry(ExecutionEntry expected)
        {
            if (EezCalldata.EntryHash(expected) != EezCalldata.EntryHash(batch.Entries[_entry++]))
                throw Invalid("Native call does not reproduce its complete settlement entry.");
        }

        private void VerifySystem(Transaction transaction, int index, UInt256 value, byte[] calldata)
        {
            if (!transaction.IsEezSystemTransaction() || transaction.SenderAddress != EezConstants.SystemAddress ||
                transaction.To != EezConstants.RollupXEezl2Address || transaction.Value != value ||
                !transaction.Data.Span.SequenceEqual(calldata) || terminal.Receipts[index].StatusCode != StatusCode.Success)
                throw Invalid("Native stream does not reproduce the executed system transaction.");
        }

        private Hash256 NextCheckpoint(int transaction)
        {
            if (_checkpoint == terminal.Checkpoints.Length || terminal.Checkpoints[_checkpoint].TransactionIndex != transaction)
                throw Invalid("Native effect does not end at the next validated checkpoint.");
            return terminal.Checkpoints[_checkpoint++].BlockHash;
        }

        public RollupXObservationClaim[] Complete()
        {
            cancellation.ThrowIfCancellationRequested();
            _l1.Complete();
            if (_transaction != terminal.Block.Transactions.Length || _checkpoint != terminal.Checkpoints.Length ||
                _entry != batch.Entries.Length || _staticEntry != batch.StaticEntries.Length || _staticEntry != _l1.StaticEntries.Count ||
                batch.ImmediateEntryCount != (UInt256)(_outbound + 1) || !batch.ImmediateStaticEntryCount.IsZero)
                throw Invalid("Native semantics do not cover the exact settlement and execution suffix.");
            RollupUpdate anchor = updates[0];
            ExecutionEntry expectedAnchor = new([anchor], default, [], [], RollingHash.SeedL1(anchor, default), rollupId, true, []);
            if (!anchor.EtherDelta.IsZero || anchor.NewRoot != terminal.Checkpoints[0].BlockHash.ValueHash256 ||
                EezCalldata.EntryHash(expectedAnchor) != EezCalldata.EntryHash(batch.Entries[0]))
                throw Invalid("Leading settlement entry is not the independently validated ordinary prefix.");
            OutboundEvent[] events = OutboundEvent.Observe(terminal.Receipts, EezConstants.RollupXEezl2Address);
            if (events.Length != _outboundEvents.Count) throw Invalid("Native stream omits or adds an outbound event.");
            for (int i = 0; i < events.Length; i++)
                if (!events[i].IsCanonical || events[i].CallGas != 0 || events[i].TransactionIndex != _outboundEvents[i].Transaction ||
                    events[i].CallHash != _outboundEvents[i].Hash) throw Invalid("Native outbound call differs from its independently executed event.");
            return _observations.ToArray();
        }
    }
}
