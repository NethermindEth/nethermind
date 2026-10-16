// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// RollupX's DA: the blob stream's <c>ChainOperation</c> carries each block's beneficiary, extra data and ordinary
/// transactions, and its brackets carry the batch's cross-chain transactions. A batch covers whole Sync intervals from a
/// Sync parent, and only a batch settled live in its own L1 slot may carry cross-chain transactions. The Sync block is
/// its ordinary prefix followed by a system transaction per effect L1 applied: each outbound load followed by the user
/// transaction its bracket carries, then the inbound deliveries.
/// </summary>
public sealed class RollupXBatchDerivation(EezSettlementContext context, EezChainRules rules) : IBatchDerivation
{
    private readonly RollupXSchedule _schedule = rules.Schedule
        ?? throw new ArgumentException("RollupX derivation needs a RollupX chain.", nameof(rules));
    private readonly EezSettlementContext _context = context;
    private readonly Address _eezl2 = rules.Eezl2Address;

    public DecodedBatch Decode(ReadOnlyMemory<byte> daStream)
    {
        NativeStream stream = NativeStream.Decode(daStream, _context.RollupId);
        OperationsPayload operations = OperationsPayload.Decode(stream.Operations);
        CrossChainTransactions transactions = CrossChainTransactions.Parse(stream.Messages, _context);
        return new Decoded(operations, transactions, this);
    }

    /// <exception cref="EezSettlementException">The batch's range is not a candidate L1 can settle in this L1 block.</exception>
    private void EnsureCandidate(BlockHeader parent, uint blocks, in L1Anchor settling, bool hasActions)
    {
        uint interval = _schedule.SyncIntervalBlocks;
        if (parent.Timestamp != _schedule.Timestamp(parent.Number) || parent.Number % interval != 0 || blocks % interval != 0)
        {
            throw new EezSettlementException(
                $"Blocks {parent.Number + 1} to {parent.Number + blocks} are not whole Sync intervals of {interval} from a Sync parent.");
        }

        RollupXAnchorKind kind = _schedule.AnchorKind(parent.Number + blocks, settling.Timestamp)
            ?? throw new EezSettlementException($"Block {parent.Number + blocks} cannot be settled by L1 block {settling.Number} at {settling.Timestamp}.");
        if (kind == RollupXAnchorKind.CatchUp && hasActions)
        {
            throw new EezSettlementException($"A batch catching up in L1 block {settling.Number} carries cross-chain transactions.");
        }
    }

    private sealed class Decoded(OperationsPayload operations, CrossChainTransactions transactions, RollupXBatchDerivation owner) : DecodedBatch
    {
        public override ulong BlockCount => operations.BlockCount;

        public override DerivedBlock[] Derive(ProducingSlice applied, in DerivationInputs inputs)
        {
            owner.EnsureCandidate(inputs.CandidateParent, operations.BlockCount, inputs.Settling, transactions.HasActions);
            byte[][] effects = transactions.Build(applied, inputs.SystemNonce, owner._context, owner._eezl2);
            return inputs.Resumed ? [Terminal(inputs, effects)] : Blocks(inputs, effects);
        }

        private DerivedBlock[] Blocks(in DerivationInputs inputs, byte[][] effects)
        {
            DerivedBlock[] blocks = new DerivedBlock[operations.BlockCount];
            int next = 0;
            foreach (OperationsRun run in operations.Runs())
            {
                for (uint i = 0; i < run.Blocks; i++)
                {
                    blocks[next++] = Block(run, inputs);
                }
            }

            DerivedBlock terminal = blocks[^1];
            blocks[^1] = terminal with { Transactions = [.. terminal.Transactions, .. effects] };
            return blocks;
        }

        private DerivedBlock Terminal(in DerivationInputs inputs, byte[][] effects)
        {
            OperationsRun last = default;
            foreach (OperationsRun run in operations.Runs())
            {
                last = run;
            }

            return new DerivedBlock(last.Beneficiary, last.ExtraData.ToArray(), effects) { PrevRandao = inputs.PrevRandao };
        }

        private static DerivedBlock Block(in OperationsRun run, in DerivationInputs inputs)
        {
            byte[][] ordinary = new byte[run.Transactions.Length][];
            for (int i = 0; i < ordinary.Length; i++)
            {
                ordinary[i] = run.Transactions[i].ToArray();
            }

            return new DerivedBlock(run.Beneficiary, run.ExtraData.ToArray(), ordinary) { PrevRandao = inputs.PrevRandao };
        }
    }
}

internal sealed class CrossChainTransactions
{
    private readonly List<(ExecutionEntry Entry, byte[] UserTransaction)> _outbound = [];
    private readonly List<ExecutionEntry> _inbound = [];

    private CrossChainTransactions(bool hasActions) => HasActions = hasActions;

    /// <summary>Whether the stream carries any cross-chain transaction, lookups included.</summary>
    public bool HasActions { get; }

    /// <exception cref="EezSettlementException">A bracket is malformed, out of order or of a shape not supported yet.</exception>
    public static CrossChainTransactions Parse(StreamMessage[] messages, EezSettlementContext context)
    {
        CrossChainTransactions transactions = new(messages.Length != 0);
        bool inbound = false;
        int index = 0;
        while (index < messages.Length)
        {
            if (messages[index++] is not StreamMessage.Initiate initiate)
            {
                throw transactions.Invalid("has no transaction boundary");
            }

            bool outbound = initiate.ChainId == context.RollupId;
            if (outbound && inbound)
            {
                throw transactions.Invalid("follows an inbound transaction");
            }

            inbound |= initiate.ChainId == EezConstants.L1RollupId;
            int calls = 0;
            while (index < messages.Length && messages[index] is not StreamMessage.Finish)
            {
                if (messages.AsSpan(index) is not [StreamMessage.Call call, StreamMessage.Return result, ..] || outbound && calls != 0)
                {
                    throw transactions.Invalid("is not a supported flat call sequence");
                }

                transactions.Add(initiate, call, result, context);
                index += 2;
                calls++;
            }

            if (calls == 0 || index == messages.Length)
            {
                throw transactions.Invalid("is empty or unfinished");
            }

            index++;
        }

        return transactions;
    }

    private int Count => _outbound.Count + _inbound.Count;

    private void Add(StreamMessage.Initiate initiate, StreamMessage.Call call, StreamMessage.Return result, EezSettlementContext context)
    {
        if (call.Gas != 0)
        {
            throw Invalid("carries call gas");
        }

        bool outbound = initiate.ChainId == context.RollupId;
        if (outbound ? _inbound.Count != 0 || call.ToChain != EezConstants.L1RollupId : initiate.ChainId != EezConstants.L1RollupId || call.ToChain != context.RollupId)
        {
            throw Invalid($"from chain {initiate.ChainId} to chain {call.ToChain} is out of order or leaves the rollup and L1");
        }

        ExecutionEntry entry = new DaAction(initiate.ChainId, call.ToChain, call.From, call.To, call.Value, 0, call.Data.ToArray(), result.Success,
            result.ReturnData.ToArray()).ToEntry(context.RollupId);
        if (!outbound)
        {
            if (result.Success)
            {
                _inbound.Add(entry);
            }

            return;
        }

        if (!result.Success)
        {
            throw new EezSettlementException(EezSettlementFailure.InvalidDaPayload, $"Cross-chain transaction {Count} is a failed outbound call, which is not supported yet.");
        }

        _outbound.Add((entry, UserTransaction(initiate.TxData, context.ChainId)));
    }

    /// <summary>The system transactions and outbound user transactions of the effects L1 applied, in Sync block order.</summary>
    public byte[][] Build(ProducingSlice applied, ulong systemNonce, EezSettlementContext context, Address eezl2)
    {
        int outboundSkip = Math.Min(applied.Skip, _outbound.Count);
        int outboundTake = Math.Min(applied.Take, _outbound.Count - outboundSkip);
        int inboundSkip = Math.Min(applied.Skip - outboundSkip, _inbound.Count);
        int inboundTake = Math.Min(Math.Max(applied.Take - outboundTake, 0), _inbound.Count - inboundSkip);
        ulong skipped = (ulong)(outboundSkip + inboundSkip);
        if (systemNonce > ulong.MaxValue - skipped)
        {
            throw new EezSettlementException("The system account nonce overflows.");
        }

        return SyncBlock.BuildTransactions(_outbound.GetRange(outboundSkip, outboundTake), _inbound.GetRange(inboundSkip, inboundTake), context.ChainId,
            context.RollupId, systemNonce + skipped, eezl2);
    }

    /// <summary>The signed L2 transaction an outbound bracket carries, which must be canonical and for this chain.</summary>
    private byte[] UserTransaction(ReadOnlyMemory<byte> encoded, ulong chainId)
    {
        Transaction transaction;
        try
        {
            RlpReader reader = new(encoded.Span);
            transaction = TxDecoder.Instance.Decode(ref reader, RlpBehaviors.ExcludeHashes | RlpBehaviors.SkipTypedWrapping) ?? throw Invalid("carries no user transaction");
        }
        catch (RlpException e)
        {
            throw Invalid($"carries a user transaction that does not decode: {e.Message}");
        }

        byte[] canonical = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
        if (!canonical.AsSpan().SequenceEqual(encoded.Span) || transaction.Type is TxType.Blob || transaction.Type == EezConstants.SystemTxType
            || (transaction.ChainId is { } id && id != chainId))
        {
            throw Invalid($"carries a user transaction that is not a canonical transaction for chain {chainId}");
        }

        return canonical;
    }

    private EezSettlementException Invalid(string reason) =>
        new(EezSettlementFailure.InvalidDaPayload, $"Cross-chain transaction {Count} {reason}.");
}
