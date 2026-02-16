// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using System;

namespace Nethermind.Xdc;

/// <summary>
/// XDC-specific transaction processor that handles special system transactions
/// like BlockSigners (0x89) which require non-standard processing.
/// 
/// In XDPoS, transactions to the BlockSigners contract (0x89) bypass normal
/// EVM execution and are handled directly by the consensus layer. This matches
/// the behavior in geth-xdc's ApplySignTransaction() function.
/// </summary>
public class XdcTransactionProcessor : TransactionProcessorBase
{
    public XdcTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider? specProvider,
        IWorldState? worldState,
        IVirtualMachine? virtualMachine,
        ICodeInfoRepository? codeInfoRepository,
        ILogManager? logManager)
        : base(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    {
    }

    protected override TransactionResult Execute(
        Transaction tx, 
        ITxTracer tracer, 
        ExecutionOptions opts)
    {
        // Check if this is a BlockSigners transaction that needs special handling
        if (IsBlockSignersTransaction(tx))
        {
            return ApplySignTransaction(tx, tracer, opts);
        }

        // Normal transaction processing
        return base.Execute(tx, tracer, opts);
    }

    /// <summary>
    /// Checks if a transaction is destined for the BlockSigners contract
    /// Must be to address 0x89 with zero value
    /// </summary>
    private bool IsBlockSignersTransaction(Transaction transaction)
    {
        return transaction.To is not null 
            && transaction.To == XdcConstants.BlockSignersAddress
            && transaction.Value.IsZero;
    }

    /// <summary>
    /// Apply BlockSigners transaction - special handling that bypasses EVM
    /// 
    /// This replicates geth-xdc's ApplySignTransaction() behavior:
    /// 1. Update nonce
    /// 2. Deduct gas cost (but gas used is 0)
    /// 3. Create receipt with BlockSigners log
    /// 4. Do NOT execute any EVM code
    /// </summary>
    private TransactionResult ApplySignTransaction(
        Transaction transaction, 
        ITxTracer tracer,
        ExecutionOptions opts)
    {
        IReleaseSpec spec = SpecProvider.GetSpec(WorldState.BlockNumber);

        try
        {
            // Get sender address
            Address sender = transaction.SenderAddress!;

            // Validate and increment nonce
            UInt256 nonce = WorldState.GetNonce(sender);
            if (nonce != transaction.Nonce)
            {
                if (tracer.IsTracingReceipt)
                {
                    tracer.MarkAsFailed(
                        sender,
                        0,
                        Array.Empty<byte>(),
                        "invalid nonce",
                        null);
                }
                return TransactionProcessor.ErrorType.WrongTransactionNonce;
            }
            WorldState.IncrementNonce(sender);

            // Calculate gas cost (but we won't actually consume the gas)
            UInt256 gasCost = (UInt256)transaction.GasLimit * transaction.GasPrice;
            
            // Deduct gas cost from sender
            if (!WorldState.BalanceEnough(sender, gasCost, spec))
            {
                if (tracer.IsTracingReceipt)
                {
                    tracer.MarkAsFailed(
                        sender,
                        0,
                        Array.Empty<byte>(),
                        "insufficient balance for gas",
                        null);
                }
                return TransactionProcessor.ErrorType.InsufficientSenderBalance;
            }
            WorldState.SubtractFromBalance(sender, gasCost, spec);

            // Refund the gas cost immediately (since gas used is 0)
            WorldState.AddToBalance(sender, gasCost, spec);

            // Create receipt - success with 0 gas used
            if (tracer.IsTracingReceipt)
            {
                tracer.MarkAsSuccess(
                    XdcConstants.BlockSignersAddress,
                    0, // Gas used = 0
                    Array.Empty<byte>(),
                    Array.Empty<LogEntry>(),
                    null);
            }

            if (Logger.IsTrace)
                Logger.Trace($"Applied BlockSigners transaction from {sender} at block {WorldState.BlockNumber}");

            // Return success (TransactionProcessor.ErrorType.None)
            return new TransactionResult();
        }
        catch (Exception ex)
        {
            if (Logger.IsError)
                Logger.Error($"Failed to apply BlockSigners transaction: {ex.Message}");
            
            if (tracer.IsTracingReceipt)
            {
                tracer.MarkAsFailed(
                    transaction.SenderAddress!,
                    0,
                    Array.Empty<byte>(),
                    ex.Message,
                    null);
            }
            
            return TransactionProcessor.ErrorType.MalformedTransaction;
        }
    }
}
