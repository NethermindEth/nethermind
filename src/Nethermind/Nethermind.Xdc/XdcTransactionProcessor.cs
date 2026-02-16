// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc;

/// <summary>
/// XDC-specific transaction processor that handles special system transactions.
/// 
/// In XDPoS, transactions to the BlockSigners contract (0x89) bypass normal
/// EVM execution and are handled directly. This matches the behavior in 
/// geth-xdc's ApplySignTransaction() function.
/// 
/// Reference: https://github.com/XinFinOrg/XDPoSChain/blob/master/core/state_processor.go
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
    /// Checks if a transaction is destined for the BlockSigners contract (0x89).
    /// Matches geth-xdc: to == BlockSignersBinary (0x89)
    /// </summary>
    private bool IsBlockSignersTransaction(Transaction transaction)
    {
        return transaction.To is not null 
            && transaction.To == XdcConstants.BlockSignersAddress;
    }

    /// <summary>
    /// Apply BlockSigners transaction - special handling that bypasses EVM.
    /// 
    /// This replicates geth-xdc's ApplySignTransaction() behavior:
    /// 1. Deduct gas cost from sender (pre-payment)
    /// 2. Get sender and validate nonce
    /// 3. Increment nonce
    /// 4. Finalize state (apply pending changes)
    /// 5. Create receipt with the ORIGINAL gas limit (for block header validation)
    /// 6. Add log entry for BlockSigners
    /// 7. Do NOT execute any EVM code
    /// 
    /// Note: The receipt shows gasUsed = tx.GasLimit (107558), not 0.
    /// This is because the gas is prepaid and must be recorded in the block header.
    /// 
    /// Reference implementation:
    /// https://github.com/XinFinOrg/XDPoSChain/blob/master/core/state_processor.go#L312
    /// </summary>
    private TransactionResult ApplySignTransaction(
        Transaction tx, 
        ITxTracer tracer,
        ExecutionOptions opts)
    {
        try
        {
            // Get current block header for context
            BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
            IReleaseSpec spec = GetSpec(header);
            
            // Get sender address (must be already set)
            Address sender = tx.SenderAddress!;
            
            // Create account if it doesn't exist (geth-xdc behavior)
            WorldState.CreateAccountIfNotExists(sender, UInt256.Zero, UInt256.Zero);
            
            // Deduct gas cost from sender (pre-payment) - IMPORTANT for state root
            UInt256 gasCost = (UInt256)tx.GasLimit * tx.GasPrice;
            if (!WorldState.BalanceEnough(sender, gasCost, spec))
            {
                return TransactionResult.InsufficientSenderBalance;
            }
            WorldState.SubtractFromBalance(sender, gasCost, spec);

            // Validate and increment nonce (geth-xdc behavior)
            UInt256 nonce = WorldState.GetNonce(sender);
            if (nonce != tx.Nonce)
            {
                if (Logger.IsDebug)
                    Logger.Debug($"BlockSigners tx invalid nonce: expected {nonce}, got {tx.Nonce}");
                
                return TransactionResult.WrongTransactionNonce;
            }
            WorldState.IncrementNonce(sender);

            // Commit state to apply changes
            WorldState.Commit(spec);

            // Create log entry for BlockSigners (matching geth-xdc)
            var logEntry = new LogEntry(
                XdcConstants.BlockSignersAddress,
                Array.Empty<byte>(),
                Array.Empty<Hash256>());

            // Report to tracer with the ORIGINAL gas limit (107558)
            // This ensures the block header gas used matches the expected value
            if (tracer.IsTracingReceipt)
            {
                var gasConsumed = new GasConsumed(tx.GasLimit, tx.GasLimit);
                tracer.MarkAsSuccess(
                    XdcConstants.BlockSignersAddress,
                    gasConsumed, // Gas used = tx.GasLimit (107558)
                    Array.Empty<byte>(),
                    new[] { logEntry });
            }

            if (Logger.IsDebug)
                Logger.Debug($"Applied BlockSigners transaction from {sender} at block {header.Number}, gas used: {tx.GasLimit}");

            // Return success
            return TransactionResult.Ok;
        }
        catch (Exception ex)
        {
            if (Logger.IsError)
                Logger.Error($"Failed to apply BlockSigners transaction: {ex.Message}");
            
            return TransactionResult.MalformedTransaction;
        }
    }
}
