// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing;

public class ParallelEoaTransferTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly BlockProcessor.BlockValidationTransactionsExecutor _fallback;
    private readonly IShareableTxProcessorSource _txProcessorSource;
    private readonly IWorldState _stateProvider;
    private readonly ISpecProvider _specProvider;
    private readonly IBlocksConfig _blocksConfig;
    private readonly ILogger _logger;
    private readonly BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? _transactionProcessedEventHandler;

    public ParallelEoaTransferTransactionsExecutor(
        ITransactionProcessorAdapter transactionProcessor,
        IWorldState stateProvider,
        IShareableTxProcessorSource txProcessorSource,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig,
        ILogManager logManager,
        BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
    {
        ArgumentNullException.ThrowIfNull(transactionProcessor);
        ArgumentNullException.ThrowIfNull(stateProvider);
        ArgumentNullException.ThrowIfNull(txProcessorSource);
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(blocksConfig);
        ArgumentNullException.ThrowIfNull(logManager);

        _fallback = new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider, transactionProcessedEventHandler);
        _txProcessorSource = txProcessorSource;
        _stateProvider = stateProvider;
        _specProvider = specProvider;
        _blocksConfig = blocksConfig;
        _logger = logManager.GetClassLogger();
        _transactionProcessedEventHandler = transactionProcessedEventHandler;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _fallback.SetBlockExecutionContext(in blockExecutionContext);
    }

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        Metrics.ResetBlockStats();

        TxReceipt[] Fallback() => _fallback.ProcessTransactions(block, processingOptions, receiptsTracer, token);

        if (!_blocksConfig.ParallelEoaTransfersOnBlockProcessing || block.Transactions.Length < 2)
        {
            return Fallback();
        }

        IReleaseSpec spec = _specProvider.GetSpec(block.Header);
        if (!TryBuildPlan(block, spec, out ParallelPlan plan))
        {
            return Fallback();
        }

        ParallelEoaResult[] results = new ParallelEoaResult[plan.TransactionCount];

        ParallelOptions options = new()
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = plan.MaxConcurrency
        };

        try
        {
            Parallel.For(0, plan.TransactionCount, options, i =>
            {
                results[i] = ExecuteTransfer(block, spec, plan, i, processingOptions);
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Debug($"Parallel EOA transfer execution failed, falling back to sequential. {ex}");
            return Fallback();
        }

        int invalidIndex = FindFirstInvalid(results);
        if (invalidIndex >= 0)
        {
            ThrowInvalidTransactionException(results[invalidIndex].Result, block.Header, block.Transactions[invalidIndex], invalidIndex);
        }

        if (!TryApplyResults(block, spec, plan, results, receiptsTracer, processingOptions))
        {
            if (_logger.IsDebug) _logger.Debug("Parallel EOA transfer execution produced unexpected deltas, falling back to sequential.");
            return Fallback();
        }

        return receiptsTracer.TxReceipts.ToArray();
    }

    private ParallelEoaResult ExecuteTransfer(Block block, IReleaseSpec spec, ParallelPlan plan, int index, ProcessingOptions processingOptions)
    {
        Transaction originalTx = block.Transactions[index];
        Transaction workingTx = new();
        originalTx.CopyTo(workingTx);

        if (processingOptions.ContainsFlag(ProcessingOptions.LoadNonceFromState) && workingTx.SenderAddress != Address.SystemUser)
        {
            workingTx.Nonce = plan.SenderNonces[index];
        }

        using IReadOnlyTxProcessingScope scope = _txProcessorSource.Build(plan.BaseHeader);
        TransactionResult result = scope.TransactionProcessor.Execute(workingTx, block.Header, NullTxTracer.Instance);

        Address sender = plan.Senders[index];
        Address recipient = plan.Recipients[index];
        IWorldState workerState = scope.WorldState;

        UInt256 senderBalance = workerState.GetBalance(sender);
        UInt256 senderNonce = workerState.GetNonce(sender);
        UInt256 recipientBalance = workerState.GetBalance(recipient);

        UInt256 beneficiaryDelta = UInt256.Zero;
        bool beneficiaryOk = true;
        UInt256 workerBeneficiary = workerState.GetBalance(plan.Beneficiary);
        if (!TrySubtract(workerBeneficiary, plan.BeneficiaryBalance, out beneficiaryDelta))
        {
            beneficiaryOk = false;
        }

        UInt256 feeCollectorDelta = UInt256.Zero;
        bool feeCollectorOk = true;
        if (plan.FeeCollector is not null)
        {
            UInt256 workerFeeCollector = workerState.GetBalance(plan.FeeCollector);
            if (!TrySubtract(workerFeeCollector, plan.FeeCollectorBalance, out feeCollectorDelta))
            {
                feeCollectorOk = false;
            }
        }

        return new ParallelEoaResult(result, workingTx.SpentGas, senderBalance, senderNonce, recipientBalance, beneficiaryDelta, feeCollectorDelta, beneficiaryOk, feeCollectorOk);
    }

    private static int FindFirstInvalid(ReadOnlySpan<ParallelEoaResult> results)
    {
        for (int i = 0; i < results.Length; i++)
        {
            if (!results[i].Result) return i;
        }

        return -1;
    }

    private bool TryApplyResults(
        Block block,
        IReleaseSpec spec,
        ParallelPlan plan,
        ReadOnlySpan<ParallelEoaResult> results,
        BlockReceiptsTracer receiptsTracer,
        ProcessingOptions processingOptions)
    {
        UInt256 beneficiaryDeltaTotal = UInt256.Zero;
        UInt256 feeCollectorDeltaTotal = UInt256.Zero;
        block.Header.GasUsed = 0;

        for (int i = 0; i < results.Length; i++)
        {
            ParallelEoaResult result = results[i];

            if (!result.BeneficiaryDeltaOk || !result.FeeCollectorDeltaOk)
            {
                return false;
            }

            Address sender = plan.Senders[i];
            Address recipient = plan.Recipients[i];

            UInt256 baseSenderBalance = plan.SenderBalances[i];
            UInt256 senderBalance = result.SenderBalance;
            if (senderBalance > baseSenderBalance)
            {
                return false;
            }

            UInt256 baseRecipientBalance = plan.RecipientBalances[i];
            UInt256 recipientBalance = result.RecipientBalance;
            if (recipientBalance < baseRecipientBalance)
            {
                return false;
            }

            if (result.SenderNonce != plan.SenderNonces[i] + UInt256.One)
            {
                return false;
            }

            UInt256 senderDelta = baseSenderBalance - senderBalance;
            if (!senderDelta.IsZero)
            {
                _stateProvider.SubtractFromBalance(sender, senderDelta, spec);
            }

            UInt256 recipientDelta = recipientBalance - baseRecipientBalance;
            if (!recipientDelta.IsZero)
            {
                _stateProvider.AddToBalanceAndCreateIfNotExists(recipient, recipientDelta, spec);
            }

            _stateProvider.SetNonce(sender, result.SenderNonce);

            Transaction originalTx = block.Transactions[i];
            if (processingOptions.ContainsFlag(ProcessingOptions.LoadNonceFromState) && originalTx.SenderAddress != Address.SystemUser)
            {
                originalTx.Nonce = plan.SenderNonces[i];
            }

            originalTx.SpentGas = result.SpentGas;
            block.Header.GasUsed += result.SpentGas;

            receiptsTracer.StartNewTxTrace(originalTx);
            receiptsTracer.MarkAsSuccess(recipient, result.SpentGas, Array.Empty<byte>(), []);
            receiptsTracer.EndTxTrace();

            if (_transactionProcessedEventHandler is not null)
            {
                _transactionProcessedEventHandler.OnTransactionProcessed(new TxProcessedEventArgs(i, originalTx, block.Header, receiptsTracer.TxReceipts[i]));
            }

            beneficiaryDeltaTotal += result.BeneficiaryDelta;
            feeCollectorDeltaTotal += result.FeeCollectorDelta;
        }

        if (!beneficiaryDeltaTotal.IsZero)
        {
            _stateProvider.AddToBalanceAndCreateIfNotExists(plan.Beneficiary, beneficiaryDeltaTotal, spec);
        }

        if (plan.FeeCollector is not null && !feeCollectorDeltaTotal.IsZero)
        {
            _stateProvider.AddToBalanceAndCreateIfNotExists(plan.FeeCollector, feeCollectorDeltaTotal, spec);
        }

        return true;
    }

    private bool TryBuildPlan(Block block, IReleaseSpec spec, out ParallelPlan plan)
    {
        plan = default;

        Address? beneficiary = block.Header.GasBeneficiary;
        if (beneficiary is null)
        {
            return false;
        }

        Address? feeCollector = spec.FeeCollector;

        Transaction[] transactions = block.Transactions;
        int txCount = transactions.Length;
        Address[] senders = new Address[txCount];
        Address[] recipients = new Address[txCount];
        UInt256[] senderBalances = new UInt256[txCount];
        UInt256[] senderNonces = new UInt256[txCount];
        UInt256[] recipientBalances = new UInt256[txCount];

        int senderSetCapacity = Math.Min(txCount, 4096);
        var senderSet = new HashSet<AddressAsKey>(senderSetCapacity);
        var recipientSet = new HashSet<AddressAsKey>(senderSetCapacity);

        for (int i = 0; i < txCount; i++)
        {
            Transaction tx = transactions[i];

            if (tx.IsSystem() || tx.IsAnchorTx || tx.IsServiceTransaction)
            {
                return false;
            }

            if (tx.IsContractCreation || tx.To is null || tx.SenderAddress is null)
            {
                return false;
            }

            if (tx.SupportsBlobs || tx.SupportsAuthorizationList || tx.HasAuthorizationList)
            {
                return false;
            }

            Address sender = tx.SenderAddress;
            Address recipient = tx.To;

            if (spec.IsPrecompile(sender) || spec.IsPrecompile(recipient))
            {
                return false;
            }

            if (_stateProvider.IsContract(sender) || _stateProvider.IsContract(recipient))
            {
                return false;
            }

            AddressAsKey senderKey = sender;
            AddressAsKey recipientKey = recipient;

            if (!senderSet.Add(senderKey) || !recipientSet.Add(recipientKey))
            {
                return false;
            }

            // Keep sender/recipient sets disjoint so transfers stay commutative.
            if (senderSet.Contains(recipientKey) || recipientSet.Contains(senderKey))
            {
                return false;
            }

            senders[i] = sender;
            recipients[i] = recipient;
        }

        AddressAsKey beneficiaryKey = beneficiary;
        if (senderSet.Contains(beneficiaryKey) || recipientSet.Contains(beneficiaryKey))
        {
            return false;
        }

        if (feeCollector is not null)
        {
            AddressAsKey feeCollectorKey = feeCollector;
            if (feeCollectorKey.Equals(beneficiaryKey) || senderSet.Contains(feeCollectorKey) || recipientSet.Contains(feeCollectorKey))
            {
                return false;
            }
        }

        for (int i = 0; i < txCount; i++)
        {
            senderBalances[i] = _stateProvider.GetBalance(senders[i]);
            senderNonces[i] = _stateProvider.GetNonce(senders[i]);
            recipientBalances[i] = _stateProvider.GetBalance(recipients[i]);
        }

        UInt256 beneficiaryBalance = _stateProvider.GetBalance(beneficiary);
        UInt256 feeCollectorBalance = feeCollector is null ? UInt256.Zero : _stateProvider.GetBalance(feeCollector);

        BlockHeader baseHeader = block.Header.Clone();
        baseHeader.StateRoot = _stateProvider.StateRoot;
        baseHeader.GasUsed = 0;

        int maxConcurrency = _blocksConfig.ParallelEoaTransfersConcurrency;
        if (maxConcurrency <= 0)
        {
            maxConcurrency = Math.Max(Environment.ProcessorCount - 1, 1);
        }

        plan = new ParallelPlan(
            txCount,
            senders,
            recipients,
            senderBalances,
            senderNonces,
            recipientBalances,
            beneficiary,
            beneficiaryBalance,
            feeCollector,
            feeCollectorBalance,
            baseHeader,
            maxConcurrency);

        return true;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction currentTx, int index)
    {
        throw new InvalidTransactionException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}", result);
    }

    private static bool TrySubtract(in UInt256 updated, in UInt256 original, out UInt256 delta)
    {
        if (updated >= original)
        {
            delta = updated - original;
            return true;
        }

        delta = UInt256.Zero;
        return false;
    }

    private readonly record struct ParallelEoaResult(
        TransactionResult Result,
        long SpentGas,
        UInt256 SenderBalance,
        UInt256 SenderNonce,
        UInt256 RecipientBalance,
        UInt256 BeneficiaryDelta,
        UInt256 FeeCollectorDelta,
        bool BeneficiaryDeltaOk,
        bool FeeCollectorDeltaOk);

    private readonly record struct ParallelPlan(
        int TransactionCount,
        Address[] Senders,
        Address[] Recipients,
        UInt256[] SenderBalances,
        UInt256[] SenderNonces,
        UInt256[] RecipientBalances,
        Address Beneficiary,
        UInt256 BeneficiaryBalance,
        Address? FeeCollector,
        UInt256 FeeCollectorBalance,
        BlockHeader BaseHeader,
        int MaxConcurrency);
}
