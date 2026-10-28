// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeBatchDerivation(EezSettlementContext context, EezChainRules rules, NativeCanonicalHistory history, IStateReader state, IEezConfig config) : IBatchDerivation
{
    private readonly EezSettlementContext _context = context;
    private readonly EezChainRules _rules = rules;
    private readonly NativeCanonicalHistory _canonical = history;
    private readonly RollupXSchedule _schedule = rules.Schedule!;
    private readonly NativeHistorySchedule _history = new(rules.Schedule!.GenesisTimestamp, rules.Schedule.BlockIntervalSeconds,
        rules.Schedule.SyncIntervalBlocks, rules.NativeHistoryOffset);

    public DecodedBatch Decode(ReadOnlyMemory<byte> daStream)
    {
        NativeStream stream = NativeStream.Decode(daStream, _context.RollupId);
        OperationsPayload operations = OperationsPayload.Decode(stream.Operations);
        if (config.MaxFollowerBatchBlocks <= 0 || operations.BlockCount > (uint)config.MaxFollowerBatchBlocks)
            throw new L1SourceIncompleteException(0, $"Native batch exceeds Eez.MaxFollowerBatchBlocks ({config.MaxFollowerBatchBlocks}).");
        return new Decoded(operations, NativeTransactions.Parse(stream.Messages, _context), this);
    }

    private NativeHistoryState ReadParent(BlockHeader parent)
    {
        if (!state.TryGetAccount(parent, EezConstants.L1HistoryAddress, out AccountStruct account) ||
            (_rules.NativeFormat == NativeSystemTxFormat.ContractV6 ? account.Nonce < 2 : account.Nonce != 1) ||
            account.CodeHash != NativeHistoryBlockExecutor.GetRuntimeHash(_rules.NativeFormat))
            throw NativeL1Header.Invalid("parent history account differs from the native profile");
        if (_rules.NativeFormat == NativeSystemTxFormat.ContractV6)
        {
            ulong storedNumber = Scalar(parent, 1);
            state.GetStorage(parent, EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(storedNumber), out UInt256 storedHash);
            return NativeProxyHistory.ResolveState(storedNumber, storedHash.ToValueHash(), _schedule);
        }
        ulong first = Scalar(parent, 1), endpoint = Scalar(parent, 2), cutoff = Scalar(parent, 3);
        if (first != _rules.Schedule!.L1Reference.Number || Scalar(parent, 4) != _history.Interval ||
            Scalar(parent, 5) != _history.Delay || Scalar(parent, 6) != NativeHistoryCall.MaxHashes)
            throw NativeL1Header.Invalid("parent history storage differs from the native profile");
        state.GetStorage(parent, EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(endpoint), out UInt256 hash);
        return new NativeHistoryState(first, endpoint, hash.ToValueHash(), cutoff);
    }

    private ulong Scalar(BlockHeader parent, ulong slot)
    {
        state.GetStorage(parent, EezConstants.L1HistoryAddress, (UInt256)slot, out UInt256 value);
        return value > ulong.MaxValue ? throw NativeL1Header.Invalid("parent history scalar exceeds uint64") : (ulong)value;
    }

    private sealed class Decoded(OperationsPayload operations, NativeTransactions transactions, NativeBatchDerivation owner) : DecodedBatch
    {
        public override ulong BlockCount => operations.BlockCount;
        public override DerivedBlock[] Derive(ProducingSlice applied, in DerivationInputs inputs) =>
            throw new InvalidOperationException("Native derivation requires canonical L1 preparation.");

        public override async ValueTask<DerivedBlock[]> DeriveAsync(ProducingSlice applied, DerivationInputs inputs, CancellationToken token)
        {
            BlockHeader parent = inputs.ExecutionParent ?? throw NativeL1Header.Invalid("missing actual execution parent");
            if (inputs.Settling.ParentHash is null)
                throw NativeL1Header.Invalid("missing actual L1 parent");
            if (inputs.AnchorSelector != ulong.MaxValue && (inputs.AnchorSelector == 0 || inputs.AnchorSelector >= inputs.Settling.Number ||
                inputs.Settling.Number - inputs.AnchorSelector > 256 || transactions.HasActions))
                throw NativeL1Header.Invalid("invalid historical settlement context or native actions");
            ulong candidateEnd = inputs.CandidateParent.Number > ulong.MaxValue - operations.BlockCount
                ? throw NativeL1Header.Invalid("native batch range overflows") : inputs.CandidateParent.Number + operations.BlockCount;
            if (inputs.Resumed ? parent.Number == ulong.MaxValue || parent.Number + 1 != candidateEnd : parent.Hash != inputs.CandidateParent.Hash)
                throw NativeL1Header.Invalid("native execution parent differs from its candidate range");
            NativeL1Header? historical = inputs.AnchorSelector == ulong.MaxValue ? null : await owner._canonical.ReadCanonical(inputs.AnchorSelector, token);
            EnsureCandidate(inputs, historical?.Timestamp ?? inputs.Settling.Timestamp);
            NativeHistoryState current = owner.ReadParent(parent);
            ulong first = historical is null ? current.Endpoint : Math.Min(current.Endpoint, inputs.AnchorSelector);
            NativeL1HeaderChain headers = await owner._canonical.Prepare(first, inputs.Settling, token);
            if (historical is not null && headers.At(historical.Number).Hash != historical.Hash)
                throw new L1SourceIncompleteException(historical.Number, "L1 reorganized the historical native anchor.");
            NativeL1Header inclusion = headers.At(inputs.Settling.Number);
            if (inputs.Settling.ParentHash is null || inclusion.ParentHash != inputs.Settling.ParentHash.ValueHash256)
                throw NativeL1Header.Invalid("missing or contradictory actual L1 parent");
            byte[][] effects = [];
            if (applied.Take != 0)
                effects = transactions.BuildNative(applied, new NativeLiveSource(owner._context.ChainId, inclusion.ParentHash), owner._rules);
            if (inputs.Resumed)
            {
                OperationsRun last = default;
                foreach (OperationsRun run in operations.Runs()) last = run;
                return [new DerivedBlock(last.Beneficiary, last.ExtraData.ToArray(), effects)
                    { PrevRandao = inputs.PrevRandao, NativeEffectsStart = effects.Length == 0 ? -1 : 0 }];
            }
            if (operations.BlockCount > int.MaxValue) throw NativeL1Header.Invalid("native block range exceeds the supported representation");
            DerivedBlock[] blocks = new DerivedBlock[operations.BlockCount];
            int next = 0;
            foreach (OperationsRun run in operations.Runs())
            {
                for (uint count = 0; count < run.Blocks; count++)
                {
                    token.ThrowIfCancellationRequested();
                    byte[][] ordinary = new byte[run.Transactions.Length][];
                    for (int index = 0; index < ordinary.Length; index++) ordinary[index] = run.Transactions[index].ToArray();
                    ulong number = parent.Number + (ulong)next + 1;
                    ulong at = owner._schedule.Timestamp(number) ?? throw NativeL1Header.Invalid("native block timestamp overflow");
                    if (owner._rules.NativeFormat == NativeSystemTxFormat.ContractV6)
                    {
                        _ = ReadImport(ordinary);
                        blocks[next++] = new DerivedBlock(run.Beneficiary, run.ExtraData.ToArray(), ordinary)
                        { NativeOriginFromParent = true };
                        continue;
                    }
                    NativeHistoryExecution execution = Transition(ordinary, at, current, headers);
                    current = current with { Endpoint = execution.OriginNumber, EndpointHash = execution.OriginHash };
                    blocks[next++] = new DerivedBlock(run.Beneficiary, run.ExtraData.ToArray(), ordinary)
                    { PrevRandao = new Hash256(execution.PrevRandao.Bytes) };
                }
            }
            DerivedBlock terminal = blocks[^1];
            blocks[^1] = terminal with
            {
                Transactions = [.. terminal.Transactions, .. effects],
                NativeEffectsStart = effects.Length == 0 ? -1 : terminal.Transactions.Length
            };
            return blocks;
        }

        private void EnsureCandidate(DerivationInputs inputs, ulong timestamp)
        {
            BlockHeader candidate = inputs.CandidateParent;
            uint interval = owner._schedule.SyncIntervalBlocks;
            if (candidate.Number > ulong.MaxValue - operations.BlockCount || candidate.Number % interval != 0 ||
                operations.BlockCount % interval != 0 || owner._schedule.Timestamp(candidate.Number) != candidate.Timestamp ||
                owner._schedule.AnchorKind(candidate.Number + operations.BlockCount, timestamp) is not { } kind ||
                kind == RollupXAnchorKind.CatchUp && transactions.HasActions)
                throw NativeL1Header.Invalid("native batch is outside its eligible Sync range");
        }

        private NativeHistoryEnvelope? ReadImport(byte[][] ordinary)
        {
            NativeHistoryEnvelope? import = null;
            for (int index = 0; index < ordinary.Length; index++)
            {
                byte[] raw = ordinary[index];
                if (raw.Length == 0) throw NativeL1Header.Invalid("empty native DA transaction");
                if (raw[0] == (byte)EezConstants.NativeSystemTxType || raw[0] == (byte)EezConstants.SystemTxType)
                    throw NativeL1Header.Invalid("cross-chain system transaction in ordinary DA");
                if (raw[0] != NativeHistoryEnvelope.Type) continue;
                if (index != 0) throw NativeL1Header.Invalid("history transaction is not transaction zero");
                import = owner._rules.NativeFormat == NativeSystemTxFormat.ContractV6
                    ? NativeHistoryEnvelope.DecodeBlocks(raw) : NativeHistoryEnvelope.Decode(raw);
                if (import.ChainId != owner._context.ChainId) throw NativeL1Header.Invalid("history envelope targets another chain");
            }
            return import;
        }

        private NativeHistoryExecution Transition(byte[][] ordinary, ulong timestamp, NativeHistoryState current, NativeL1HeaderChain headers)
        {
            NativeHistoryEnvelope? import = ReadImport(ordinary);
            if (import is not null && current.Endpoint > ulong.MaxValue - (ulong)import.Hashes.Length)
                throw NativeL1Header.Invalid("history endpoint overflow");
            ulong? selected = import is null ? null : current.Endpoint + (ulong)import.Hashes.Length;
            NativeHistoryExecution execution = owner._history.VerifyContractV3(timestamp, in current, headers, selected);
            if (import is not null)
                for (int index = 0; index < import.Hashes.Length; index++)
                    if (import.Hashes[index] != headers.At(current.Endpoint + (ulong)index + 1).Hash)
                        throw NativeL1Header.Invalid("recorded history differs from canonical L1");
            return execution;
        }
    }
}
