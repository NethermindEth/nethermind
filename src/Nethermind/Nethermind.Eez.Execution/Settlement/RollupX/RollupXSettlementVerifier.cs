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
using SignedInt256 = Nethermind.Int256.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal static class RollupXSettlementVerifier
{
    public static RollupXObservationClaim[] Verify(PostBatch batch, NativeStream stream,
        IReadOnlyList<EezStatelessBlockResult> window, ulong rollupId, CancellationToken cancellation = default)
    {
        RollupXOperationsCheck.Verify(OperationsPayload.Decode(stream.Operations), window, cancellation);
        RollupUpdate[] updates = RollupUpdateChain.Verify(batch, rollupId, window[0].Block.ParentHash!.ValueHash256, window[^1].Hash.ValueHash256);
        EezStatelessBlockResult terminal = window[^1];
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(terminal.Block);
        NativeTransaction[] transactions = Parse(stream.Messages, rollupId, cancellation);
        Verification verification = new(batch, updates, terminal, plan, rollupId, cancellation);
        foreach (NativeTransaction transaction in transactions) verification.VerifyTransaction(transaction);
        return verification.Complete();
    }

    private static NativeTransaction[] Parse(StreamMessage[] messages, ulong rollupId, CancellationToken cancellation)
    {
        List<NativeTransaction> transactions = [];
        bool inbound = false;
        int index = 0;
        while (index < messages.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            if (messages[index++] is not StreamMessage.Initiate start || start.ChainId != 0 && start.ChainId != rollupId)
                throw Invalid("Native transaction has an invalid origin.");
            if (start.ChainId == rollupId && inbound) throw Invalid("Outbound native transaction follows inbound execution.");
            inbound |= start.ChainId == 0;
            if (start.ChainId == 0 && !start.TxData.IsEmpty) throw Invalid("L1-origin transaction publishes unauthenticated transaction bytes.");
            List<NativeCall> calls = [];
            while (index < messages.Length && messages[index] is not StreamMessage.Finish)
            {
                cancellation.ThrowIfCancellationRequested();
                NativeCall call = messages[index++] switch
                {
                    StreamMessage.Call item => new(item.ToChain, new CrossChainCall(0, false, item.Gas, item.From, start.ChainId,
                        item.To, item.Value, item.Data.ToArray()), false, []),
                    StreamMessage.StaticCall item => new(item.ToChain, new CrossChainCall(0, true, item.Gas, item.From, start.ChainId,
                        item.To, UInt256.Zero, item.Data.ToArray()), false, []),
                    _ => throw Invalid("Nested calls and rollback regions require semantic tree validation."),
                };
                if (call.TargetRollup != (start.ChainId == 0 ? rollupId : 0) || call.Call.Gas != 0)
                    throw Invalid("Native call has unsupported routing or gas.");
                if (index == messages.Length || messages[index++] is not StreamMessage.Return result)
                    throw Invalid("Native call has no terminal result.");
                calls.Add(call with { Success = result.Success, Output = result.ReturnData.ToArray() });
            }
            if (calls.Count == 0 || index == messages.Length) throw Invalid("Native transaction is empty or unfinished.");
            index++;
            transactions.Add(new NativeTransaction(start.ChainId, start.TxData, calls.ToArray()));
        }
        return transactions.ToArray();
    }

    private static EezSettlementException Invalid(string message) => new(message);

    private sealed class Verification(PostBatch batch, RollupUpdate[] updates, EezStatelessBlockResult terminal,
        RollupXCheckpointPlan plan, ulong rollupId, CancellationToken cancellation)
    {
        private readonly List<RollupXObservationClaim> _observations = [];
        private readonly List<(int Transaction, ValueHash256 Hash)> _outboundEvents = [];
        private int _transaction = plan.OrdinaryTransactions;
        private int _checkpoint = 1;
        private int _entry = 1;
        private int _staticEntry;
        private int _outbound;
        private bool _inbound;
        private ValueHash256 _liveRoot = terminal.Checkpoints[0].BlockHash.ValueHash256;

        public void VerifyTransaction(NativeTransaction transaction)
        {
            cancellation.ThrowIfCancellationRequested();
            if (transaction.Origin != 0) VerifyOutbound(transaction);
            else
            {
                _inbound = true;
                foreach (NativeCall call in transaction.Calls) VerifyInbound(call);
            }
        }

        private void VerifyOutbound(NativeTransaction transaction)
        {
            if (_inbound || _entry >= batch.Entries.Length || _transaction + 1 >= terminal.Block.Transactions.Length)
                throw Invalid("Outbound transaction has no matching settlement entry and load/user pair.");
            Transaction load = terminal.Block.Transactions[_transaction];
            Transaction user = terminal.Block.Transactions[_transaction + 1];
            if (user.IsEezSystemTransaction() || user.SenderAddress == EezConstants.SystemAddress ||
                !TxDecoder.Instance.Encode(user, RlpBehaviors.SkipTypedWrapping).Bytes.AsSpan().SequenceEqual(transaction.Data.Span))
                throw Invalid("Native outbound transaction bytes differ from canonical execution.");
            List<L2ExecutionEntry> source = [];
            List<L2StaticExecutionEntry> sourceStatic = [];
            CrossChainCall[] calls = new CrossChainCall[transaction.Calls.Length];
            RollupUpdate update = updates[_entry];
            ValueHash256 rolling = RollingHash.SeedL1(update, default);
            SignedInt256 delta = default;
            for (int i = 0; i < transaction.Calls.Length; i++)
            {
                NativeCall call = transaction.Calls[i];
                ValueHash256 hash = CallHash(call);
                calls[i] = call.Call;
                rolling = RollingHash.CallEnd(RollingHash.CallBegin(rolling, hash), call.Success, call.Output);
                if (call.Call.IsStatic)
                    sourceStatic.Add(new L2StaticExecutionEntry((UInt256)source.Count, hash, [], default, call.Success, call.Output));
                else
                {
                    source.Add(new L2ExecutionEntry(hash, [], [], RollingHash.SeedL2(hash), call.Success, call.Output));
                    if (call.Success)
                    {
                        _outboundEvents.Add((_transaction + 1, hash));
                        SignedInt256.Subtract(delta, EtherDelta.Credit(call.Call.Value), out delta);
                    }
                }
            }
            VerifySystem(load, _transaction, UInt256.Zero,
                EezCalldata.EncodeLoadExecutionTable(new ExecutionTable(source.ToArray(), sourceStatic.ToArray())));
            Hash256 checkpoint = NextCheckpoint(_transaction + 1);
            if (update.EtherDelta != delta || update.CurrentRoot != _liveRoot || update.NewRoot != checkpoint.ValueHash256)
                throw Invalid("Outbound value movement or root update differs from canonical execution.");
            MatchEntry(new ExecutionEntry([update], default, calls, [], rolling, rollupId, true, []));
            _liveRoot = update.NewRoot;
            _transaction += 2;
            _outbound++;
        }

        private void VerifyInbound(NativeCall call)
        {
            ValueHash256 hash = CallHash(call);
            if (call.Call.IsStatic)
            {
                if (_staticEntry == batch.StaticEntries.Length) throw Invalid("Native static call has no settlement entry.");
                StaticExecutionEntry expected = new([new ExpectedRoot(rollupId, _liveRoot)], hash, [], default, rollupId, call.Success, call.Output);
                if (EezCalldata.StaticEntryHash(expected) != EezCalldata.StaticEntryHash(batch.StaticEntries[_staticEntry++]))
                    throw Invalid("Native static call differs from its settlement entry.");
                Observe(call, ObservationMode.Static);
                return;
            }
            if (_entry >= batch.Entries.Length) throw Invalid("Native inbound call has no settlement entry.");
            RollupUpdate update = updates[_entry];
            if (update.CurrentRoot != _liveRoot) throw Invalid("Inbound call starts from a different FIFO root.");
            if (call.Success)
            {
                if (_transaction == terminal.Block.Transactions.Length) throw Invalid("Successful inbound call has no canonical delivery.");
                L2ExecutionEntry delivery = new(hash, [call.Call], [], RollingHash.SingleL2Call(hash, true, call.Output), true, call.Output);
                VerifySystem(terminal.Block.Transactions[_transaction], _transaction, call.Call.Value,
                    EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall([delivery], [])));
                if (update.NewRoot != NextCheckpoint(_transaction).ValueHash256 || update.EtherDelta != EtherDelta.Credit(call.Call.Value))
                    throw Invalid("Inbound value movement or root update differs from canonical execution.");
                _transaction++;
            }
            else
            {
                if (update.NewRoot != _liveRoot || !update.EtherDelta.IsZero)
                    throw Invalid("Failed inbound lookup changes the canonical commitment or value.");
                Observe(call, ObservationMode.Lookup);
            }
            MatchEntry(new ExecutionEntry([update], hash, [], [], RollingHash.SeedL1(update, hash), rollupId, call.Success, call.Output));
            _liveRoot = update.NewRoot;
        }

        private void Observe(NativeCall call, ObservationMode mode) =>
            _observations.Add(new RollupXObservationClaim(new Hash256(_liveRoot.Bytes), call.Call.SourceAddress,
                call.Call.SourceRollupId, call.Call.TargetAddress, call.Call.Data, call.Call.Value, mode, new ObservationOutcome(call.Success, call.Output)));

        private static ValueHash256 CallHash(NativeCall call) => CrossChainCallHash.Compute(call.Call.IsStatic,
            call.Call.SourceAddress, call.Call.SourceRollupId, call.Call.TargetAddress, call.TargetRollup,
            call.Call.Value, 0, call.Call.Data);

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
            if (_transaction != terminal.Block.Transactions.Length || _checkpoint != terminal.Checkpoints.Length ||
                _entry != batch.Entries.Length || _staticEntry != batch.StaticEntries.Length ||
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

    private sealed record NativeTransaction(ulong Origin, ReadOnlyMemory<byte> Data, NativeCall[] Calls);
    private sealed record NativeCall(ulong TargetRollup, CrossChainCall Call, bool Success, byte[] Output);
}
