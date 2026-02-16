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
    /// 1. Get sender and validate nonce
    /// 2. Increment nonce
    /// 3. Finalize state (apply pending changes)
    /// 4. Create receipt with 0 gas used
    /// 5. Add log entry for BlockSigners
    /// 6. Do NOT execute any EVM code
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
            
            // Validate and increment nonce (geth-xdc behavior)
            UInt256 nonce = WorldState.GetNonce(sender);
            if (nonce != tx.Nonce)
            {
                if (Logger.IsDebug)
                    Logger.Debug($"BlockSigners tx invalid nonce: expected {nonce}, got {tx.Nonce}");
                
                return TransactionResult.WrongTransactionNonce;
            }
            WorldState.IncrementNonce(sender);

            // Commit state to apply the nonce change
            WorldState.Commit(spec);

            // Create log entry for BlockSigners (matching geth-xdc)
            var logEntry = new LogEntry(
                XdcConstants.BlockSignersAddress,
                Array.Empty<byte>(),
                Array.Empty<Hash256>());
            
            // Use the tracer to report the log if needed
            if (tracer.IsTracingReceipt)
            {
                // Report as success with 0 gas used
                var gasConsumed = new GasConsumed(0, 0);
                tracer.MarkAsSuccess(
                    XdcConstants.BlockSignersAddress,
                    gasConsumed, // Gas used = 0
                    Array.Empty<byte>(),
                    new[] { logEntry });
            }

            if (Logger.IsDebug)
                Logger.Debug($"Applied BlockSigners transaction from {sender} at block {header.Number}");

            // Return success - 0 gas used, no error
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
