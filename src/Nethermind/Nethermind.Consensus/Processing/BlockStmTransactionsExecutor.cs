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
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing;

public class BlockStmTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ParallelEoaTransferTransactionsExecutor _fallback;
    private readonly IShareableTxProcessorSource _txProcessorSource;
    private readonly IWorldState _stateProvider;
    private readonly ISpecProvider _specProvider;
    private readonly IBlocksConfig _blocksConfig;
    private readonly ILogger _logger;
    private readonly BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? _transactionProcessedEventHandler;

    public BlockStmTransactionsExecutor(
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

        _fallback = new ParallelEoaTransferTransactionsExecutor(transactionProcessor, stateProvider, txProcessorSource, specProvider, blocksConfig, logManager, transactionProcessedEventHandler);
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

        if (!_blocksConfig.BlockStmOnBlockProcessing || block.Transactions.Length < 2)
        {
            return Fallback();
        }

        IReleaseSpec spec = _specProvider.GetSpec(block.Header);
        if (!IsBlockSupported(block))
        {
            return Fallback();
        }

        if (!TryGetBaseStateRoot(out Hash256 baseStateRoot))
        {
            return Fallback();
        }

        BlockHeader baseHeader = block.Header.Clone();
        baseHeader.StateRoot = baseStateRoot;
        baseHeader.GasUsed = 0;

        int txCount = block.Transactions.Length;
        StmExecutionResult[] results = new StmExecutionResult[txCount];

        ParallelOptions options = new()
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = GetMaxConcurrency()
        };

        try
        {
            Parallel.For(0, txCount, options, i =>
            {
                results[i] = ExecuteTransaction(block, baseHeader, i, processingOptions);
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Debug($"Block-STM execution failed, falling back to sequential. {ex}");
            return Fallback();
        }

        Address? beneficiary = block.Header.GasBeneficiary;
        if (beneficiary is null)
        {
            return Fallback();
        }

        Address? feeCollector = spec.FeeCollector;

        if (!TryPrepareAdditiveAccounts(results, beneficiary, feeCollector, out AdditiveAccountPlan additivePlan))
        {
            return Fallback();
        }

        if (!IsConflictFree(results, additivePlan))
        {
            return Fallback();
        }

        int invalidIndex = FindFirstInvalid(results);
        if (invalidIndex >= 0)
        {
            ThrowInvalidTransactionException(results[invalidIndex].Result, block.Header, block.Transactions[invalidIndex], invalidIndex);
        }

        if (!TryApplyResults(block, spec, results, receiptsTracer, processingOptions, additivePlan))
        {
            if (_logger.IsDebug) _logger.Debug("Block-STM produced unexpected deltas, falling back to sequential.");
            return Fallback();
        }

        return receiptsTracer.TxReceipts.ToArray();
    }

    private bool IsBlockSupported(Block block)
    {
        foreach (Transaction tx in block.Transactions)
        {
            if (tx.IsSystem() || tx.IsAnchorTx || tx.IsServiceTransaction)
            {
                return false;
            }

            if (tx.SupportsBlobs || tx.SupportsAuthorizationList || tx.HasAuthorizationList)
            {
                return false;
            }
        }

        return true;
    }

    private int GetMaxConcurrency()
    {
        int maxConcurrency = _blocksConfig.BlockStmConcurrency;
        if (maxConcurrency <= 0)
        {
            maxConcurrency = Math.Max(Environment.ProcessorCount - 1, 1);
        }

        return maxConcurrency;
    }

    private bool TryGetBaseStateRoot(out Hash256 stateRoot)
    {
        try
        {
            stateRoot = _stateProvider.StateRoot;
            return true;
        }
        catch (InvalidOperationException)
        {
            try
            {
                _stateProvider.RecalculateStateRoot();
                stateRoot = _stateProvider.StateRoot;
                return true;
            }
            catch (InvalidOperationException)
            {
                stateRoot = default;
                return false;
            }
        }
    }

    private StmExecutionResult ExecuteTransaction(Block block, BlockHeader baseHeader, int index, ProcessingOptions processingOptions)
    {
        Transaction originalTx = block.Transactions[index];
        Transaction workingTx = new();
        originalTx.CopyTo(workingTx);

        UInt256? baseNonce = null;
        if (processingOptions.ContainsFlag(ProcessingOptions.LoadNonceFromState) && workingTx.SenderAddress != Address.SystemUser)
        {
            baseNonce = _stateProvider.GetNonce(workingTx.SenderAddress!);
            workingTx.Nonce = baseNonce.Value;
        }

        using IReadOnlyTxProcessingScope scope = _txProcessorSource.Build(baseHeader);
        StmTxTracer tracer = new();
        BlockHeader txHeader = baseHeader.Clone();
        TransactionResult result = scope.TransactionProcessor.Execute(workingTx, txHeader, tracer);

        return new StmExecutionResult(index, originalTx, result, tracer, baseNonce);
    }

    private static bool TryPrepareAdditiveAccounts(
        ReadOnlySpan<StmExecutionResult> results,
        Address beneficiary,
        Address? feeCollector,
        out AdditiveAccountPlan additivePlan)
    {
        additivePlan = new AdditiveAccountPlan(beneficiary, feeCollector);

        foreach (ref readonly StmExecutionResult result in results)
        {
            if (!additivePlan.TryAccumulate(result))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsConflictFree(ReadOnlySpan<StmExecutionResult> results, in AdditiveAccountPlan additivePlan)
    {
        HashSet<AddressAsKey> writeAccounts = [];
        HashSet<StorageCell> writeStorage = [];

        foreach (ref readonly StmExecutionResult result in results)
        {
            if (Overlaps(result.Trace.ReadAccounts, writeAccounts) || Overlaps(result.Trace.ReadStorage, writeStorage))
            {
                return false;
            }

            if (Overlaps(result.Trace.WriteStorage, writeStorage))
            {
                return false;
            }

            if (!result.Trace.TryGetWriteAccounts(additivePlan, out HashSet<AddressAsKey> txWrites))
            {
                return false;
            }

            if (Overlaps(txWrites, writeAccounts))
            {
                return false;
            }

            writeAccounts.UnionWith(txWrites);
            writeStorage.UnionWith(result.Trace.WriteStorage);
        }

        return true;
    }

    private static bool Overlaps(HashSet<AddressAsKey> current, HashSet<AddressAsKey> existing)
    {
        foreach (AddressAsKey item in current)
        {
            if (existing.Contains(item))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Overlaps(HashSet<StorageCell> current, HashSet<StorageCell> existing)
    {
        foreach (StorageCell item in current)
        {
            if (existing.Contains(item))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindFirstInvalid(ReadOnlySpan<StmExecutionResult> results)
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
        ReadOnlySpan<StmExecutionResult> results,
        BlockReceiptsTracer receiptsTracer,
        ProcessingOptions processingOptions,
        in AdditiveAccountPlan additivePlan)
    {
        block.Header.GasUsed = 0;

        foreach (ref readonly StmExecutionResult result in results)
        {
            Transaction tx = result.OriginalTx;
            if (tx.GasLimit > block.Header.GasLimit - block.Header.GasUsed)
            {
                ThrowInvalidTransactionException(TransactionResult.BlockGasLimitExceeded, block.Header, tx, result.Index);
            }

            if (!result.Trace.ApplyStateChanges(_stateProvider, spec, additivePlan))
            {
                return false;
            }

            if (processingOptions.ContainsFlag(ProcessingOptions.LoadNonceFromState) && tx.SenderAddress != Address.SystemUser && result.BaseNonce is not null)
            {
                tx.Nonce = result.BaseNonce.Value;
            }

            tx.SpentGas = result.Trace.SpentGas;
            block.Header.GasUsed += result.Trace.SpentGas;

            receiptsTracer.StartNewTxTrace(tx);
            if (result.Trace.Success)
            {
                receiptsTracer.MarkAsSuccess(result.Trace.Recipient, result.Trace.GasConsumed, Array.Empty<byte>(), result.Trace.Logs);
            }
            else
            {
                receiptsTracer.MarkAsFailed(result.Trace.Recipient, result.Trace.GasConsumed, Array.Empty<byte>(), result.Trace.Error);
            }
            receiptsTracer.EndTxTrace();

            _transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(result.Index, tx, block.Header, receiptsTracer.TxReceipts[result.Index]));
        }

        additivePlan.Apply(_stateProvider, spec);
        return true;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction currentTx, int index)
    {
        throw new InvalidTransactionException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}", result);
    }

    private readonly record struct StmExecutionResult(
        int Index,
        Transaction OriginalTx,
        TransactionResult Result,
        StmTxTracer Trace,
        UInt256? BaseNonce);

    private sealed class StmTxTracer : TxTracer
    {
        public override bool IsTracingState => true;
        public override bool IsTracingStorage => true;
        public override bool IsTracingReceipt => true;

        public HashSet<AddressAsKey> ReadAccounts { get; } = [];
        public HashSet<StorageCell> ReadStorage { get; } = [];
        public HashSet<StorageCell> WriteStorage { get; } = [];
        public Dictionary<AddressAsKey, AccountChange> AccountChanges { get; } = [];
        public Dictionary<StorageCell, StorageChange> StorageChanges { get; } = [];

        public Address Recipient { get; private set; }
        public GasConsumed GasConsumed { get; private set; }
        public long SpentGas => GasConsumed.SpentGas;
        public bool Success { get; private set; }
        public string? Error { get; private set; }
        public LogEntry[] Logs { get; private set; } = Array.Empty<LogEntry>();

        public override void ReportAccountRead(Address address)
        {
            ReadAccounts.Add(address);
        }

        public override void ReportStorageRead(in StorageCell storageCell)
        {
            ReadStorage.Add(storageCell);
        }

        public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            UpdateAccountChange(address, static (ref AccountChange change, UInt256? beforeValue, UInt256? afterValue) =>
            {
                change.BalanceBefore = beforeValue;
                change.BalanceAfter = afterValue;
            }, before, after);
        }

        public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            UpdateAccountChange(address, static (ref AccountChange change, UInt256? beforeValue, UInt256? afterValue) =>
            {
                change.NonceBefore = beforeValue;
                change.NonceAfter = afterValue;
            }, before, after);
        }

        public override void ReportCodeChange(Address address, byte[]? before, byte[]? after)
        {
            UpdateAccountChange(address, static (ref AccountChange change, byte[]? beforeValue, byte[]? afterValue) =>
            {
                change.CodeBefore = beforeValue;
                change.CodeAfter = afterValue;
            }, before, after);
        }

        public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            StorageChanges[storageCell] = new StorageChange(before, after);
            WriteStorage.Add(storageCell);
        }

        public override void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
        }

        public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            Recipient = recipient;
            GasConsumed = gasSpent;
            Logs = logs;
            Success = true;
        }

        public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            Recipient = recipient;
            GasConsumed = gasSpent;
            Logs = Array.Empty<LogEntry>();
            Error = error;
            Success = false;
        }

        public bool TryGetWriteAccounts(in AdditiveAccountPlan additivePlan, out HashSet<AddressAsKey> writes)
        {
            writes = new HashSet<AddressAsKey>(AccountChanges.Count);
            foreach ((AddressAsKey address, AccountChange change) in AccountChanges)
            {
                if (additivePlan.ShouldSkipAccount(address))
                {
                    continue;
                }

                writes.Add(address);
            }

            return true;
        }

        public bool ApplyStateChanges(IWorldState stateProvider, IReleaseSpec spec, in AdditiveAccountPlan additivePlan)
        {
            foreach ((AddressAsKey address, AccountChange change) in AccountChanges)
            {
                if (additivePlan.ShouldSkipAccount(address))
                {
                    continue;
                }

                if (!ApplyAccountChange(stateProvider, address, change, spec))
                {
                    return false;
                }
            }

            foreach ((StorageCell cell, StorageChange change) in StorageChanges)
            {
                if (additivePlan.ShouldSkipStorage(cell))
                {
                    continue;
                }

                stateProvider.CreateAccountIfNotExists(cell.Address, UInt256.Zero);
                stateProvider.Set(cell, change.After ?? Array.Empty<byte>());
            }

            return true;
        }

        private delegate void AccountChangeUpdater<T>(ref AccountChange change, T beforeValue, T afterValue);

        private void UpdateAccountChange<T>(Address address, AccountChangeUpdater<T> update, T beforeValue, T afterValue)
        {
            AddressAsKey key = address;
            AccountChange change = AccountChanges.TryGetValue(key, out AccountChange existing) ? existing : default;
            update(ref change, beforeValue, afterValue);
            AccountChanges[key] = change;
        }

        private static bool ApplyAccountChange(IWorldState stateProvider, AddressAsKey addressKey, AccountChange change, IReleaseSpec spec)
        {
            Address address = addressKey;

            if (change.IsDeleted)
            {
                stateProvider.DeleteAccount(address);
                stateProvider.ClearStorage(address);
                return true;
            }

            if (!stateProvider.AccountExists(address) && change.HasAnyAfter)
            {
                UInt256 balance = change.BalanceAfter ?? UInt256.Zero;
                UInt256 nonce = change.NonceAfter ?? UInt256.Zero;
                stateProvider.CreateAccountIfNotExists(address, balance, nonce);
            }

            if (change.BalanceAfter is not null)
            {
                if (change.BalanceBefore is null)
                {
                    stateProvider.AddToBalanceAndCreateIfNotExists(address, change.BalanceAfter.Value, spec);
                }
                else
                {
                    UInt256 before = change.BalanceBefore.Value;
                    UInt256 after = change.BalanceAfter.Value;
                    if (after > before)
                    {
                        stateProvider.AddToBalance(address, after - before, spec);
                    }
                    else if (before > after)
                    {
                        stateProvider.SubtractFromBalance(address, before - after, spec);
                    }
                }
            }

            if (change.NonceAfter is not null)
            {
                stateProvider.SetNonce(address, change.NonceAfter.Value);
            }

            if (change.CodeAfter is not null)
            {
                byte[] code = change.CodeAfter;
                ValueHash256 codeHash = Keccak.Compute(code).ValueHash256;
                stateProvider.InsertCode(address, codeHash, code, spec);
            }

            return true;
        }
    }

    private struct AccountChange
    {
        public UInt256? BalanceBefore { get; set; }
        public UInt256? BalanceAfter { get; set; }
        public UInt256? NonceBefore { get; set; }
        public UInt256? NonceAfter { get; set; }
        public byte[]? CodeBefore { get; set; }
        public byte[]? CodeAfter { get; set; }

        public bool HasAnyAfter => BalanceAfter is not null || NonceAfter is not null || CodeAfter is not null;
        public bool IsDeleted => BalanceAfter is null && NonceAfter is null && CodeAfter is null && HasAnyBefore;
        private bool HasAnyBefore => BalanceBefore is not null || NonceBefore is not null || CodeBefore is not null;
    }

    private readonly record struct StorageChange(byte[]? Before, byte[]? After);

    private struct AdditiveAccountPlan
    {
        private readonly Address _beneficiary;
        private readonly Address? _feeCollector;
        private readonly bool _allowBeneficiary;
        private readonly bool _allowFeeCollector;
        private UInt256 _beneficiaryDelta;
        private UInt256 _feeCollectorDelta;

        public AdditiveAccountPlan(Address beneficiary, Address? feeCollector)
        {
            _beneficiary = beneficiary;
            _feeCollector = feeCollector;
            _allowBeneficiary = true;
            _allowFeeCollector = feeCollector is not null;
            _beneficiaryDelta = UInt256.Zero;
            _feeCollectorDelta = UInt256.Zero;
        }

        public bool TryAccumulate(in StmExecutionResult result)
        {
            StmTxTracer trace = result.Trace;

            if (_allowBeneficiary && !TryAccumulateAdditive(trace, _beneficiary, ref _beneficiaryDelta))
            {
                return false;
            }

            if (_allowFeeCollector && _feeCollector is not null && !TryAccumulateAdditive(trace, _feeCollector, ref _feeCollectorDelta))
            {
                return false;
            }

            return true;
        }

        public bool ShouldSkipAccount(AddressAsKey address)
        {
            AddressAsKey beneficiaryKey = _beneficiary;
            if (_allowBeneficiary && address.Equals(beneficiaryKey))
            {
                return true;
            }

            if (_allowFeeCollector && _feeCollector is not null)
            {
                AddressAsKey feeCollectorKey = _feeCollector;
                return address.Equals(feeCollectorKey);
            }

            return false;
        }

        public bool ShouldSkipStorage(in StorageCell storageCell)
        {
            if (_allowBeneficiary && storageCell.Address.Equals(_beneficiary))
            {
                return true;
            }

            return _allowFeeCollector && _feeCollector is not null && storageCell.Address.Equals(_feeCollector);
        }

        public void Apply(IWorldState stateProvider, IReleaseSpec spec)
        {
            if (!_beneficiaryDelta.IsZero)
            {
                stateProvider.AddToBalanceAndCreateIfNotExists(_beneficiary, _beneficiaryDelta, spec);
            }

            if (_feeCollector is not null && !_feeCollectorDelta.IsZero)
            {
                stateProvider.AddToBalanceAndCreateIfNotExists(_feeCollector, _feeCollectorDelta, spec);
            }
        }

        private bool TryAccumulateAdditive(StmTxTracer trace, Address additiveAddress, ref UInt256 deltaTotal)
        {
            AddressAsKey additiveKey = additiveAddress;

            if (trace.ReadAccounts.Contains(additiveKey))
            {
                return false;
            }

            foreach (StorageCell storageCell in trace.StorageChanges.Keys)
            {
                if (storageCell.Address.Equals(additiveAddress))
                {
                    return false;
                }
            }

            if (trace.AccountChanges.TryGetValue(additiveKey, out AccountChange change))
            {
                if (change.NonceBefore != change.NonceAfter || change.CodeBefore != change.CodeAfter)
                {
                    return false;
                }

                if (change.BalanceAfter is null && change.BalanceBefore is not null)
                {
                    return false;
                }

                if (change.BalanceBefore is not null && change.BalanceAfter is not null)
                {
                    if (change.BalanceAfter.Value < change.BalanceBefore.Value)
                    {
                        return false;
                    }

                    deltaTotal += change.BalanceAfter.Value - change.BalanceBefore.Value;
                }
                else if (change.BalanceBefore is null && change.BalanceAfter is not null)
                {
                    deltaTotal += change.BalanceAfter.Value;
                }
            }

            return true;
        }
    }
}
