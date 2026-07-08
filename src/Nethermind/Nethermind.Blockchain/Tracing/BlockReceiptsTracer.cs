// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public class BlockReceiptsTracer(bool parallel = false) : IBlockTracer, ITxTracer, IJournal<int>, ITxTracerWrapper
{
    private IBlockTracer _otherTracer = NullBlockTracer.Instance;
    protected Block Block = null!;
    public bool IsTracingReceipt => true;
    public bool IsTracingActions => _currentTxTracer.IsTracingActions;
    public bool IsTracingOpLevelStorage => _currentTxTracer.IsTracingOpLevelStorage;
    public bool IsTracingMemory => _currentTxTracer.IsTracingMemory;
    public bool IsTracingInstructions => _currentTxTracer.IsTracingInstructions;
    public bool IsTracingRefunds => _currentTxTracer.IsTracingRefunds;
    public bool IsTracingReturnData => _currentTxTracer.IsTracingReturnData;
    public bool IsTracingCode => _currentTxTracer.IsTracingCode;
    public bool IsTracingStack => _currentTxTracer.IsTracingStack;
    public bool IsTracingState => _currentTxTracer.IsTracingState;
    public bool IsTracingStorage => _currentTxTracer.IsTracingStorage;

    public bool IsTracingBlockHash => _currentTxTracer.IsTracingBlockHash;
    public bool IsTracingAccess => _currentTxTracer.IsTracingAccess;
    public bool IsTracingFees => _currentTxTracer.IsTracingFees;
    public bool IsTracingLogs => _currentTxTracer.IsTracingLogs;

    public void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        _txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));

        // hacky way to support nested receipt tracers
        if (_otherTracer is ITxTracer otherTxTracer)
        {
            otherTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        }

        if (_currentTxTracer.IsTracingReceipt)
        {
            _currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        }
    }

    public void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        _txReceipts.Add(BuildFailedReceipt(recipient, gasSpent, error, stateRoot));

        // hacky way to support nested receipt tracers
        if (_otherTracer is ITxTracer otherTxTracer)
        {
            otherTxTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        }

        if (_currentTxTracer.IsTracingReceipt)
        {
            _currentTxTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        }
    }

    protected TxReceipt BuildFailedReceipt(Address recipient, in GasConsumed gasSpent, string error, Hash256? stateRoot)
    {
        TxReceipt receipt = BuildReceipt(recipient, gasSpent, StatusCode.Failure, [], stateRoot);
        receipt.Error = error;
        return receipt;
    }

    /// <summary>
    /// Updates cumulative gas tracking for both block and receipt accounting.
    /// EIP-7778: Block gas uses pre-refund values for gas limit accounting,
    /// while receipt gas uses post-refund values (what users actually pay).
    /// </summary>
    /// <returns>The cumulative post-refund gas for receipts</returns>
    protected ulong UpdateCumulativeGasTracking(in GasConsumed gasConsumed)
    {
        // Track cumulative block gas for restore (regular + EIP-8037 state)
        (ulong prevRegular, ulong prevState) = _cumulativeBlockGasPerTx.Count > 0 ? _cumulativeBlockGasPerTx[^1] : (0, 0);
        ulong cumulativeBlockGas = prevRegular + gasConsumed.EffectiveBlockGas;
        ulong cumulativeBlockStateGas = prevState + gasConsumed.BlockStateGas;
        _cumulativeBlockGasPerTx.Add((cumulativeBlockGas, cumulativeBlockStateGas));

        // EIP-8037: block gasUsed = max(sum_regular, sum_state). Override header accumulation.
        if (!parallel)
        {
            Block.Header.GasUsed = EthereumGasPolicy.CombineBlockGas(cumulativeBlockGas, cumulativeBlockStateGas);
        }

        // Track cumulative receipt gas (post-refund)
        _cumulativeReceiptGas += gasConsumed.SpentGas;

        Debug.Assert(_txReceipts.Count + 1 == _cumulativeBlockGasPerTx.Count,
            "Receipt and gas tracking lists must remain synchronized");

        return _cumulativeReceiptGas;
    }

    protected virtual TxReceipt BuildReceipt(Address recipient, in GasConsumed gasConsumed, byte statusCode, LogEntry[] logEntries, Hash256? stateRoot)
    {
        ulong cumulativeReceiptGas = UpdateCumulativeGasTracking(gasConsumed);

        Transaction transaction = CurrentTx!;
        // Diagnostic-only: effective gas price after EIP-1559 baseFee adjustment.
        // Computed eagerly so the diagnostic dump (which doesn't run through the
        // ReceiptForRpc pipeline) doesn't see effectiveGasPrice as null.
        UInt256 baseFee = Block.Header.BaseFeePerGas;
        UInt256 effectiveGasPrice = transaction.CalculateEffectiveGasPrice(eip1559Enabled: baseFee > 0, baseFee);
        TxReceipt txReceipt = new()
        {
            Logs = logEntries,
            TxType = transaction.Type,
            // Bloom calculated in parallel with other receipts
            GasUsedTotal = cumulativeReceiptGas,  // Post-refund cumulative
            StatusCode = statusCode,
            Recipient = transaction.IsContractCreation ? null : recipient,
            BlockHash = Block.Hash,
            BlockNumber = Block.Number,
            Index = _currentIndex,
            GasUsed = gasConsumed.SpentGas,  // Post-refund for this tx
            EffectiveGasPrice = effectiveGasPrice,
            Sender = transaction.SenderAddress,
            ContractAddress = transaction.IsContractCreation ? recipient : null,
            TxHash = transaction.Hash,
            PostTransactionState = stateRoot
        };

        // EIP-7778: regular-dimension block accounting introduces the
        // pre-refund/post-refund split. BlockGasUsed is pre-refund; ExecutionGasUsed
        // (= OperationGas) is post-refund without EIP-7976 floor.
        if (gasConsumed.BlockGas > 0)
        {
            txReceipt.BlockGasUsed = gasConsumed.EffectiveBlockGas;
            txReceipt.ExecutionGasUsed = gasConsumed.OperationGas;
        }

        // EIP-8037: state-dim block accounting. Only set when the tx actually consumed
        // state gas - state-untouching txs leave StorageGasUsed at zero.
        if (gasConsumed.BlockStateGas > 0)
        {
            txReceipt.StorageGasUsed = gasConsumed.BlockStateGas;
        }

        return txReceipt;
    }

    public void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env) =>
        _currentTxTracer.StartOperation(pc, opcode, gas, env);

    public void ReportOperationError(EvmExceptionType error) =>
        _currentTxTracer.ReportOperationError(error);


    public void ReportOperationRemainingGas(ulong gas) =>
        _currentTxTracer.ReportOperationRemainingGas(gas);

    public void ReportLog(LogEntry log) =>
        _currentTxTracer.ReportLog(log);

    public void SetOperationMemorySize(ulong newSize) =>
        _currentTxTracer.SetOperationMemorySize(newSize);

    public void SetOperationReturnData(ReadOnlyMemory<byte> returnData) =>
        _currentTxTracer.SetOperationReturnData(returnData);

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data) =>
        _currentTxTracer.ReportMemoryChange(offset, data);

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) =>
        _currentTxTracer.ReportStorageChange(key, value);

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) =>
        _currentTxTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) =>
        _currentTxTracer.LoadOperationStorage(address, storageIndex, value);

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) =>
        _currentTxTracer.ReportSelfDestruct(address, balance, refundAddress);

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) =>
        _currentTxTracer.ReportBalanceChange(address, before, after);

    public void ReportCodeChange(Address address, byte[] before, byte[] after) =>
        _currentTxTracer.ReportCodeChange(address, before, after);

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after) =>
        _currentTxTracer.ReportNonceChange(address, before, after);

    public void ReportAccountRead(Address address) =>
        _currentTxTracer.ReportAccountRead(address);

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) =>
        _currentTxTracer.ReportStorageChange(storageCell, before, after);

    public void ReportStorageRead(in StorageCell storageCell) =>
        _currentTxTracer.ReportStorageRead(storageCell);

    public void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) =>
        _currentTxTracer.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

    public void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) =>
        _currentTxTracer.ReportActionEnd(gas, output);

    public void ReportActionError(EvmExceptionType exceptionType) =>
        _currentTxTracer.ReportActionError(exceptionType);

    public void ReportActionRevert(ulong gasLeft, ReadOnlyMemory<byte> output) =>
        _currentTxTracer.ReportActionRevert(gasLeft, output);

    public void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) =>
        _currentTxTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode) =>
        _currentTxTracer.ReportByteCode(byteCode);

    public void ReportGasUpdateForVmTrace(ulong refund, ulong gasAvailable) =>
        _currentTxTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);

    public void ReportRefund(long refund) =>
        _currentTxTracer.ReportRefund(refund);

    public void ReportExtraGasPressure(ulong extraGasPressure) =>
        _currentTxTracer.ReportExtraGasPressure(extraGasPressure);

    public void ReportAccess(IEnumerable<Address> accessedAddresses, IEnumerable<StorageCell> accessedStorageCells) =>
        _currentTxTracer.ReportAccess(accessedAddresses, accessedStorageCells);

    public void SetOperationStack(TraceStack stack) =>
        _currentTxTracer.SetOperationStack(stack);

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem) =>
        _currentTxTracer.ReportStackPush(stackItem);

    public void ReportBlockHash(Hash256 blockHash) =>
        _currentTxTracer.ReportBlockHash(blockHash);

    public void SetOperationMemory(TraceMemory memoryTrace) =>
        _currentTxTracer.SetOperationMemory(memoryTrace);

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        if (_currentTxTracer.IsTracingFees)
        {
            _currentTxTracer.ReportFees(fees, burntFees);
        }
    }

    private ITxTracer _currentTxTracer = NullTxTracer.Instance;
    protected int _currentIndex { get; private set; }
    private readonly List<TxReceipt> _txReceipts = [];
    private readonly List<(ulong Regular, ulong State)> _cumulativeBlockGasPerTx = [];  // Track pre-refund block gas for restore (regular + EIP-8037 state)
    private ulong _cumulativeReceiptGas;  // Track cumulative post-refund gas for receipts
    protected Transaction? CurrentTx;
    public ReadOnlySpan<TxReceipt> TxReceipts => CollectionsMarshal.AsSpan(_txReceipts);
    public TxReceipt LastReceipt => _txReceipts[^1];
    public IBlockTracer OtherTracer => _otherTracer;

    /// <summary>
    /// Diagnostic-only: place a receipt at a specific tx index, leaving any prior gaps as null.
    /// Leaves <c>_cumulativeBlockGasPerTx</c> alone, and forwards to a wrapped receipts tracer so
    /// invalid-block dumps see receipts harvested from parallel workers.
    /// </summary>
    public void SetReceipt(int index, TxReceipt receipt)
    {
        if (_txReceipts.Count <= index) CollectionsMarshal.SetCount(_txReceipts, index + 1);
        _txReceipts[index] = receipt;

        if (!ReferenceEquals(_otherTracer, this) && _otherTracer is BlockReceiptsTracer receiptsTracer)
        {
            receiptsTracer.SetReceipt(index, receipt);
        }
    }

    /// <summary>
    /// EIP-8037: cumulative state gas for the last tracked tx.
    /// Used by parallel execution to pass state gas back for 2D block gas accounting.
    /// </summary>
    public ulong BlockStateGasUsed => _cumulativeBlockGasPerTx.Count > 0 ? _cumulativeBlockGasPerTx[^1].State : 0;
    public bool IsTracingRewards => _otherTracer.IsTracingRewards;
    public ulong CumulativeRegularGasUsed => _cumulativeBlockGasPerTx.Count > 0 ? _cumulativeBlockGasPerTx[^1].Regular : 0;

    public ITxTracer InnerTracer => _currentTxTracer;

    public int TakeSnapshot() => _txReceipts.Count;

    public void Restore(int snapshot)
    {
        int numToRemove = _txReceipts.Count - snapshot;
        if (numToRemove > 0)
        {
            _txReceipts.RemoveRange(snapshot, numToRemove);
            _cumulativeBlockGasPerTx.RemoveRange(snapshot, numToRemove);
        }

        Debug.Assert(_txReceipts.Count == _cumulativeBlockGasPerTx.Count,
            "Receipt and gas tracking lists must remain synchronized after restore");

        // Restore block gas from tracking: max(cumulative_regular, cumulative_state) for EIP-8037
        (ulong cumulativeRegular, ulong cumulativeState) = _cumulativeBlockGasPerTx.Count > 0 ? _cumulativeBlockGasPerTx[^1] : (0, 0);
        Block.Header.GasUsed = EthereumGasPolicy.CombineBlockGas(cumulativeRegular, cumulativeState);

        // Restore receipt gas from remaining receipts (post-refund)
        _cumulativeReceiptGas = _txReceipts.Count > 0 ? _txReceipts[^1].GasUsedTotal : 0;
    }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        _otherTracer.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block)
    {
        Block = block;
        _currentIndex = 0;
        CurrentTx = null;
        _currentTxTracer = NullTxTracer.Instance;
        _txReceipts.Clear();
        _cumulativeBlockGasPerTx.Clear();
        _cumulativeReceiptGas = 0;

        _otherTracer.StartNewBlockTrace(block);
    }

    /// <summary>
    /// Resets a pooled per-transaction tracer without sending block-start events to a
    /// previously attached tracer. Parallel worker tracers attach the shared
    /// parallel-safe tracer after reset, matching the old one-shot tracer setup.
    /// </summary>
    public void ResetForParallelTx(Block block, IBlockTracer otherTracer)
    {
        _otherTracer = NullBlockTracer.Instance;
        StartNewBlockTrace(block);
        if (otherTracer != NullBlockTracer.Instance)
        {
            SetOtherTracer(otherTracer);
        }
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        CurrentTx = tx;
        _currentTxTracer = _otherTracer.StartNewTxTrace(tx);
        return _currentTxTracer;
    }

    public void EndTxTrace()
    {
        _otherTracer.EndTxTrace();
        _currentIndex++;
    }

    public void EndBlockTrace() => EndBlockTrace(accumulateBlockBloom: true);

    // Callers that compute the header bloom on a background thread pass false: the accumulation
    // below reads per-receipt blooms, which that thread is concurrently writing.
    public void EndBlockTrace(bool accumulateBlockBloom)
    {
        _otherTracer.EndBlockTrace();
        if (accumulateBlockBloom && _txReceipts.Count > 0)
        {
            Bloom blockBloom = new();
            Block.Header.Bloom = blockBloom;
            for (int index = 0; index < _txReceipts.Count; index++)
            {
                TxReceipt? receipt = _txReceipts[index];
                if (receipt is not null)
                {
                    blockBloom.Accumulate(receipt.Bloom!);
                }
            }
        }
        _otherTracer = NullBlockTracer.Instance;
    }

    public void SetOtherTracer(IBlockTracer blockTracer)
    {
        ArgumentNullException.ThrowIfNull(blockTracer);
        _otherTracer = blockTracer;
    }

    public void Dispose() => _currentTxTracer.Dispose();
}
