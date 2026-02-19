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
/// In XDPoS, transactions to system contracts (0x88, 0x89, 0x90) have special handling:
/// - BlockSigners (0x89): Bypass normal EVM execution entirely
/// - Validator (0x88): Skip balance validation for staking operations
/// - Randomize (0x90): Skip balance validation
/// 
/// This matches the behavior in geth-xdc which allows these consensus-level
/// transactions even when the sender has insufficient balance.
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

    // TIPSigning fork block for XDC mainnet - special handling only applies after this block
    private const long TIPSigningBlock = 3_000_000;

    protected override TransactionResult Execute(
        Transaction tx, 
        ITxTracer tracer, 
        ExecutionOptions opts)
    {
        // Get current block header to check block number
        BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
        
        // Only apply special BlockSigners handling after TIPSigning fork (block 3,000,000)
        // Before this fork, BlockSigners transactions are processed normally through EVM
        if (header.Number >= TIPSigningBlock && IsBlockSignersTransaction(tx))
        {
            return ApplySignTransaction(tx, tracer, opts);
        }

        // Normal transaction processing (EVM execution)
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
    /// Checks if a transaction is destined for an XDPoS system contract.
    /// System contracts include:
    /// - Validator (0x88): Masternode staking/voting
    /// - BlockSigners (0x89): Block signing records  
    /// - Randomize (0x90): Randomization for validator selection
    /// 
    /// XDC geth allows transactions to these contracts even when the sender
    /// has insufficient balance, as they are consensus-level operations.
    /// </summary>
    private bool IsXdcSystemContractTransaction(Transaction transaction)
    {
        if (transaction.To is null) return false;
        
        return transaction.To == XdcConstants.ValidatorAddress ||
               transaction.To == XdcConstants.BlockSignersAddress ||
               transaction.To == XdcConstants.RandomizeAddress;
    }
    
    /// <summary>
    /// Override BuyGas to skip balance validation for XDPoS system contract transactions.
    /// 
    /// XDC geth allows validator staking transactions (to 0x88) even when the sender
    /// has insufficient balance. This is because these are consensus-level operations
    /// that transfer value as part of masternode staking, not regular user transfers.
    /// 
    /// This fixes the sync issue at block 1,755,834 on Apothem where a 10M XDC
    /// validator staking tx was sent from an account with 0 balance.
    /// 
    /// Issue: https://github.com/AnilChinchawale/nethermind/issues/38
    /// </summary>
    protected override TransactionResult BuyGas(
        Transaction tx, 
        IReleaseSpec spec, 
        ITxTracer tracer, 
        ExecutionOptions opts,
        in UInt256 effectiveGasPrice, 
        out UInt256 premiumPerGas, 
        out UInt256 senderReservedGasPayment, 
        out UInt256 blobBaseFee)
    {
        // For XDPoS system contract transactions, skip balance validation
        // This matches geth-xdc behavior where validator/staking operations
        // are allowed even with insufficient sender balance
        if (IsXdcSystemContractTransaction(tx))
        {
            if (Logger.IsDebug)
                Logger.Debug($"XDC system contract tx to {tx.To}, skipping balance validation");
            
            // Force skip validation for XDPoS system contracts
            opts |= ExecutionOptions.SkipValidation;
        }
        
        return base.BuyGas(tx, spec, tracer, opts, in effectiveGasPrice, 
            out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);
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
            UInt256 senderBalance = WorldState.GetBalance(sender);
            if (senderBalance < gasCost)
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

            // Increment header.GasUsed to match expected block gas
            // This is critical - without this, block validation fails with HeaderGasUsedMismatch
            header.GasUsed += tx.GasLimit;

            // Create log entry for BlockSigners (matching geth-xdc)
            var logEntry = new LogEntry(
                XdcConstants.BlockSignersAddress,
                Array.Empty<byte>(),
                Array.Empty<Hash256>());

            // Report to tracer with the ORIGINAL gas limit (107558)
            // This ensures the receipt cumulative gas used matches
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
