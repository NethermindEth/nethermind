// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Executes the recorded history transition before applications under its selected L1 randomness.</summary>
/// <remarks>
/// Evidence belongs to the serialized execution session and must be ready before execution.
/// Final anchor authorization and block-level rollback remain the caller's responsibility.
/// </remarks>
internal sealed class NativeHistoryBlockExecutor(
    IBlockProcessor.IBlockTransactionsExecutor inner,
    IWorldState state,
    ISpecProvider specProvider,
    EezChainRules rules,
    NativeHistorySchedule schedule,
    ulong firstNumber,
    NativeHistorySource? source,
    NativeL1HeaderChain evidence) : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly NativeHistorySource? _source = rules.UsesNativeContract
        ? null
        : source ?? throw new ArgumentNullException(nameof(source));

    // Canonical L1BlockhashContract runtime from the native-v1 profile.
    internal static readonly ValueHash256 RuntimeHash = new("0x88192d264757958a6e14725aa40c875814def72c1f8710e5f72c768356f8afa0");

    private static readonly ValueHash256 ContractV3RuntimeHash = new("0xef41c0d934734d41e8eed6c11a19015ea84cd5cce35e52f5fdd5e5fe30d8dac9");

    private static readonly ValueHash256 ContractV4RuntimeHash = new("0xbe389fdaa7ed5833a65c0ceffacb8e04e727127f8faf6e240611272d566b6676");

    internal static ValueHash256 GetRuntimeHash(NativeSystemTxFormat format) => format switch
    {
        NativeSystemTxFormat.RecordedV1 => RuntimeHash,
        NativeSystemTxFormat.ContractV3 => ContractV3RuntimeHash,
        NativeSystemTxFormat.ContractV4 => ContractV4RuntimeHash,
        NativeSystemTxFormat.ContractV6 => NativeProxyHistory.RuntimeHash,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!rules.UsesNativeSystemTransactions || block.Number == 0 || block.Header.GasUsed != 0)
            throw new InvalidBlockException(block, "Native history requires a fresh native-profile block.");
        NativeHistoryState prior = ReadState();
        Transaction? import = FindImport(block);
        NativeHistoryExecution execution = rules.UsesNativeContract
            ? VerifyContractV3(block, in prior, import)
            : schedule.VerifyRecorded(block.Timestamp, in prior, evidence, import?.Data);
        if (import is not null)
        {
            if (!import.HasSystemTransactionFields(rules) || import.ChainId != specProvider.ChainId ||
                import.GasLimit != Math.Min(block.GasLimit, EezConstants.L1HistoryTxGasCap) ||
                !rules.UsesNativeContract && (block.ParentHash is null ||
                import.SourceHash!.ValueHash256 != _source!.Compute(block.ParentHash.ValueHash256, block.Number, block.Timestamp, import.Data.Span)))
                throw new InvalidBlockException(block, "Native history transaction differs from the recorded transition.");
        }
        if (block.Header.MixHash?.ValueHash256 != execution.PrevRandao)
            throw new InvalidBlockException(block, "L2 PREVRANDAO differs from the selected L1 origin.");
        BlockExecutionContext context = BlockExecutionContext.WithPrevRandao(block.Header, specProvider.GetSpec(block.Header), execution.PrevRandao);
        inner.SetBlockExecutionContext(in context);
        TxReceipt[] receipts = inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
        if (import is not null && (receipts.Length == 0 || receipts[0].StatusCode != StatusCode.Success || receipts[0].Logs is { Length: > 0 }))
            throw new InvalidBlockException(block, "Mandatory native history import failed or emitted logs.");
        NativeHistoryState after = ReadState();
        ulong nextCutoff = rules.UsesNativeContract || import is null ? prior.ScheduleCutoff : execution.Cutoff + schedule.Interval;
        if (after != new NativeHistoryState(prior.FirstNumber, execution.OriginNumber, execution.OriginHash, nextCutoff))
            throw new InvalidBlockException(block, "Native history post-state differs from the recorded transition.");
        return receipts;
    }

    private Transaction? FindImport(Block block)
    {
        Transaction? import = null;
        for (int index = 0; index < block.Transactions.Length; index++)
        {
            Transaction tx = block.Transactions[index];
            bool history = rules.UsesNativeContract
                ? (byte)tx.Type == NativeHistoryEnvelope.Type
                : tx.Type == EezConstants.NativeSystemTxType && tx.To == EezConstants.L1HistoryAddress;
            if (!history) continue;
            if (index != 0) throw new InvalidBlockException(block, "Native history import must be transaction zero.");
            import = tx;
        }
        return import;
    }

    private NativeHistoryExecution VerifyContractV3(Block block, in NativeHistoryState prior, Transaction? import)
    {
        NativeExecutionGroup group = default;
        foreach (Transaction tx in block.Transactions) group = group.After(tx, out _);
        group.EnsureComplete();
        NativeHistoryTransaction? history = import as NativeHistoryTransaction;
        if (import is not null && history is null) throw new InvalidBlockException(block, "Invalid dedicated history transaction.");
        ulong? selected = null;
        if (history is not null)
        {
            ulong count = (ulong)history.Envelope.Hashes.Length;
            if (prior.Endpoint > ulong.MaxValue - count) throw NativeL1Header.Invalid("history endpoint overflow");
            selected = prior.Endpoint + count;
        }
        NativeHistoryExecution execution = schedule.VerifyContractV3(block.Timestamp, in prior, evidence, selected);
        if (history is not null)
        {
            ReadOnlySpan<ValueHash256> hashes = history.Envelope.Hashes;
            for (int index = 0; index < hashes.Length; index++)
                if (hashes[index] != evidence.At(prior.Endpoint + (ulong)index + 1).Hash)
                    throw NativeL1Header.Invalid("history transaction differs from authenticated headers");
        }
        return execution;
    }

    private NativeHistoryState ReadState()
    {
        if (state.GetNonce(EezConstants.L1HistoryAddress) != 1 || state.GetCodeHash(EezConstants.L1HistoryAddress) != GetRuntimeHash(rules.NativeFormat) ||
            ValueKeccak.Compute(state.GetCode(EezConstants.L1HistoryAddress).Span) != GetRuntimeHash(rules.NativeFormat))
            throw NativeL1Header.Invalid("incorrect history runtime or nonce");
        ulong first = Scalar(1);
        ulong endpoint = Scalar(2);
        ulong next = Scalar(3);
        if (first != firstNumber || Scalar(4) != schedule.Interval || Scalar(5) != schedule.Delay || Scalar(6) != NativeHistoryCall.MaxHashes)
            throw NativeL1Header.Invalid("history storage differs from fixed profile");
        state.Get(new StorageCell(EezConstants.L1HistoryAddress, HashSlot(endpoint)), out UInt256 hash);
        return new NativeHistoryState(first, endpoint, hash.ToValueHash(), next);
    }

    private ulong Scalar(ulong slot)
    {
        state.Get(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)slot), out UInt256 value);
        if (value > ulong.MaxValue) throw NativeL1Header.Invalid("history scalar exceeds uint64");
        return (ulong)value;
    }

    internal static UInt256 HashSlot(ulong number)
    {
        Span<byte> bytes = stackalloc byte[2 * AbiWord.Size];
        AbiWriter writer = new(bytes);
        writer.Write(number);
        writer.Write(0UL);
        return new UInt256(ValueKeccak.Compute(bytes).Bytes, isBigEndian: true);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(in context);
    public void PublishTransactionProcessedEvents() => inner.PublishTransactionProcessedEvents();
    public void ClearTransactionProcessedEvents() => inner.ClearTransactionProcessedEvents();
    public void SetupTxTimingMetrics(Block block) => inner.SetupTxTimingMetrics(block);
    public long StartTxTimer() => inner.StartTxTimer();
    public void StopTxTimer(int index, long start) => inner.StopTxTimer(index, start);
}
