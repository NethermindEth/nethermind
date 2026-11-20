// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Arkiv.Native;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Arkiv.Execution;

/// <summary>
/// Arkiv runs no EVM: a user transaction is a transfer, a call to the entity engine at <c>0x44…44</c>, or refused.
/// Inside a block each transaction takes the outcome Arkiv's executor gave it (<see cref="ArkivBlockContext"/>); outside
/// one — <c>eth_call</c>, <c>eth_estimateGas</c> — it is run by the same executor at the store's head, without committing.
/// System calls (EIP-4788, EIP-2935, EIP-7002/7251) still go to the EVM, as they do on <c>arkiv-reth</c>.
/// </summary>
public sealed class ArkivTransactionProcessor(ITransactionProcessor evm, ArkivBlockContext context, ArkivChain chain) : ITransactionProcessor
{
    private BlockHeader? _header;

    public void SetBlockExecutionContext(BlockHeader blockHeader)
    {
        _header = blockHeader;
        evm.SetBlockExecutionContext(blockHeader);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _header = blockExecutionContext.Header;
        evm.SetBlockExecutionContext(in blockExecutionContext);
    }

    public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions opts)
    {
        if (transaction.IsSystem()) return evm.Process(transaction, txTracer, opts);
        if (context.Build is { } build) return Build(build, transaction, txTracer);
        if (context.Current is not null) return Replay(transaction, txTracer, opts);
        // The prewarmer runs a block's transactions ahead to load trie nodes; Arkiv's state is native, nothing to warm.
        if (opts.HasFlag(ExecutionOptions.Warmup)) return TransactionResult.Ok;
        // eth_call and eth_estimateGas restore (CommitAndRestore), simulation builds up: both ask a question at the head.
        if (opts.HasFlag(ExecutionOptions.Restore) || opts.HasFlag(ExecutionOptions.BuildUp)) return Call(transaction, txTracer);
        // What is left re-runs a past transaction off its block to trace it (Commit | SkipValidation). An Arkiv
        // transaction is no EVM execution, so that would answer with outcomes it never had; arkiv-reth has no tracing.
        if (opts.HasFlag(ExecutionOptions.SkipValidation)) return TransactionResult.WithDetail(TransactionResult.ErrorType.MalformedTransaction, NotTraceable);
        return Call(transaction, txTracer);
    }

    public const string NotTraceable = "Arkiv transactions run no EVM and cannot be traced";

    private TransactionResult Replay(Transaction transaction, ITxTracer txTracer, ExecutionOptions opts)
    {
        (ArkivReceipt receipt, ulong before) = context.TakeNext();
        ulong spent = receipt.CumulativeGasUsed - before;
        if (!opts.HasFlag(ExecutionOptions.SkipValidation) && _header is not null) _header.GasUsed += spent;
        if (!opts.HasFlag(ExecutionOptions.Warmup))
        {
            transaction.SpentGas = spent;
            transaction.BlockGasUsed = spent;
        }

        if (txTracer.IsTracingReceipt)
        {
            Address recipient = transaction.To ?? Address.Zero;
            GasConsumed gas = new(spent, spent);
            if (receipt.Success) txTracer.MarkAsSuccess(recipient, gas, [], receipt.Logs);
            else txTracer.MarkAsFailed(recipient, gas, [], "execution reverted");
        }
        return TransactionResult.Ok;
    }

    /// <summary>Building: Arkiv's executor takes the transaction or refuses it, and a refused one is skipped.</summary>
    private TransactionResult Build(ArkivBuild build, Transaction transaction, ITxTracer txTracer)
    {
        (ArkivReceipt? receipt, ulong spent, string? refused) = build.Apply(transaction);
        if (receipt is null) return TransactionResult.WithDetail(TransactionResult.ErrorType.MalformedTransaction, refused!);
        if (_header is not null) _header.GasUsed += spent;
        transaction.SpentGas = spent;
        transaction.BlockGasUsed = spent;
        if (txTracer.IsTracingReceipt)
        {
            Address recipient = transaction.To ?? Address.Zero;
            GasConsumed gas = new(spent, spent);
            if (receipt.Success) txTracer.MarkAsSuccess(recipient, gas, [], receipt.Logs);
            else txTracer.MarkAsFailed(recipient, gas, [], "execution reverted");
        }
        return TransactionResult.Ok;
    }

    private TransactionResult Call(Transaction transaction, ITxTracer txTracer)
    {
        BlockHeader header = _header ?? throw new InvalidOperationException("Arkiv: a call needs a block context");
        JsonElement response;
        try
        {
            response = chain.Call(new
            {
                from = (transaction.SenderAddress ?? Address.Zero).ToString(),
                to = transaction.To?.ToString(),
                data = transaction.Data.ToArray().ToHexString(true),
                value = transaction.Value.ToHexString(false),
                gas = transaction.GasLimit,
                gasPrice = (ulong)Math.Min((decimal)ulong.MaxValue, (decimal)transaction.GasPrice),
                nonce = transaction.Nonce,
                blockNumber = header.Number,
                timestamp = header.Timestamp,
                baseFee = (ulong)header.BaseFeePerGas,
                blockGasLimit = header.GasLimit,
                beneficiary = (header.Beneficiary ?? Address.Zero).ToString(),
            });
        }
        catch (ArkivNativeException e)
        {
            return TransactionResult.WithDetail(TransactionResult.ErrorType.MalformedTransaction, e.NativeMessage);
        }

        ulong gasUsed = response.GetProperty("gasUsed").GetUInt64();
        byte[] output = Bytes.FromHexString(response.GetProperty("output").GetString() ?? "0x");
        string status = response.GetProperty("status").GetString()!;
        if (txTracer.IsTracingReceipt)
        {
            Address recipient = transaction.To ?? Address.Zero;
            GasConsumed gas = new(gasUsed, gasUsed);
            if (status == "success") txTracer.MarkAsSuccess(recipient, gas, output, []);
            // As the EVM path reports a revert: its decoded Error(string) reason, or the bare sentinel.
            else if (status == "revert") txTracer.MarkAsFailed(recipient, gas, output, TransactionSubstate.GetErrorMessage(output) ?? TransactionSubstate.Revert);
            else txTracer.MarkAsFailed(recipient, gas, output, response.GetProperty("error").GetString());
        }
        transaction.SpentGas = gasUsed;
        return status == "revert" ? TransactionResult.EvmException(EvmExceptionType.Revert, "execution reverted") : TransactionResult.Ok;
    }
}
