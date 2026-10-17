// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement.RollupX;

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
        NativeTransactions transactions = NativeTransactions.Parse(stream.Messages, _context);
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

    private sealed class Decoded(OperationsPayload operations, NativeTransactions transactions, RollupXBatchDerivation owner) : DecodedBatch
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
