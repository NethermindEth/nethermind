// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public class BlockReceiptsTracer : IBlockTracer, ITxTracer, IJournal<int>, ITxTracerWrapper
{
    private IBlockTracer _otherTracer = NullBlockTracer.Instance;
    protected Block Block = null!;
    public bool IsTracingReceipt => true;
    public bool IsTracingActions => _currentTxTracer.IsTracingActions;
    public bool IsTracingOpLevelStorage => _currentTxTracer.IsTracingOpLevelStorage;
    public bool IsTracingMemory => _currentTxTracer.IsTracingMemory;
    public bool IsTracingInstructions => _currentTxTracer.IsTracingInstructions;
    public bool IsTracingRefunds => _currentTxTracer.IsTracingRefunds;
    public bool IsTracingCode => _currentTxTracer.IsTracingCode;
    public bool IsTracingStack => _currentTxTracer.IsTracingStack;
    public bool IsTracingState => _currentTxTracer.IsTracingState;
    public bool IsTracingStorage => _currentTxTracer.IsTracingStorage;

    public bool IsTracingBlockHash => _currentTxTracer.IsTracingBlockHash;
    public bool IsTracingAccess => _currentTxTracer.IsTracingAccess;
    public bool IsTracingFees => _currentTxTracer.IsTracingFees;
    public bool IsTracingLogs => _currentTxTracer.IsTracingLogs;

    public void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        _txReceipts.Add(BuildReceipt(recipient, gasSpent.SpentGas, StatusCode.Success, logs, stateRoot));

        // hacky way to support nested receipt tracers
        if (_otherTracer is ITxTracer otherTxTracer)
        {
            otherTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        }

        if (_currentTxTracer.IsTracingReceipt)
        {
            // TODO: is no stateRoot a bug?
            _currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs);
        }
    }

    public void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        _txReceipts.Add(BuildFailedReceipt(recipient, gasSpent.SpentGas, error, stateRoot));

        // hacky way to support nested receipt tracers
        if (_otherTracer is ITxTracer otherTxTracer)
        {
            otherTxTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        }

        if (_currentTxTracer.IsTracingReceipt)
        {
            // TODO: is no stateRoot a bug?
            _currentTxTracer.MarkAsFailed(recipient, gasSpent, output, error, null);
        }
    }

    protected TxReceipt BuildFailedReceipt(Address recipient, long gasSpent, string error, Hash256? stateRoot)
    {
        TxReceipt receipt = BuildReceipt(recipient, gasSpent, StatusCode.Failure, [], stateRoot);
        receipt.Error = error;
        return receipt;
    }

    protected virtual TxReceipt BuildReceipt(Address recipient, long spentGas, byte statusCode, LogEntry[] logEntries, Hash256? stateRoot)
    {
        Transaction transaction = CurrentTx!;
        TxReceipt txReceipt = new()
        {
            Logs = logEntries,
            TxType = transaction.Type,
            // Bloom calculated in parallel with other receipts
            GasUsedTotal = Block.GasUsed,
            StatusCode = statusCode,
            Recipient = transaction.IsContractCreation ? null : recipient,
            BlockHash = Block.Hash,
            BlockNumber = Block.Number,
            Index = _currentIndex,
            GasUsed = spentGas,
            Sender = transaction.SenderAddress,
            ContractAddress = transaction.IsContractCreation ? recipient : null,
            TxHash = transaction.Hash,
            PostTransactionState = stateRoot
        };

        return txReceipt;
    }

    public void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0) =>
        _currentTxTracer.StartOperation(pc, opcode, gas, env, codeSection, functionDepth);

    public void ReportOperationError(EvmExceptionType error) =>
        _currentTxTracer.ReportOperationError(error);


    public void ReportOperationRemainingGas(long gas) =>
        _currentTxTracer.ReportOperationRemainingGas(gas);

    public void ReportLog(LogEntry log) =>
        _currentTxTracer.ReportLog(log);

    public void SetOperationMemorySize(ulong newSize) =>
        _currentTxTracer.SetOperationMemorySize(newSize);

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

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) =>
        _currentTxTracer.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output) =>
        _currentTxTracer.ReportActionEnd(gas, output);

    public void ReportActionError(EvmExceptionType exceptionType) =>
        _currentTxTracer.ReportActionError(exceptionType);

    public void ReportActionRevert(long gasLeft, ReadOnlyMemory<byte> output) =>
        _currentTxTracer.ReportActionRevert(gasLeft, output);

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) =>
        _currentTxTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode) =>
        _currentTxTracer.ReportByteCode(byteCode);

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable) =>
        _currentTxTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);

    public void ReportRefund(long refund) =>
        _currentTxTracer.ReportRefund(refund);

    public void ReportExtraGasPressure(long extraGasPressure) =>
        _currentTxTracer.ReportExtraGasPressure(extraGasPressure);

    public void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells) =>
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
    private readonly List<TxReceipt> _txReceipts = new();
    protected Transaction? CurrentTx;
    public IReadOnlyList<TxReceipt> TxReceipts => _txReceipts;
    public TxReceipt LastReceipt => _txReceipts[^1];
    public bool IsTracingRewards => _otherTracer.IsTracingRewards;

    public ITxTracer InnerTracer => _currentTxTracer;

    public int TakeSnapshot() => _txReceipts.Count;

    public void Restore(int snapshot)
    {
        int numToRemove = _txReceipts.Count - snapshot;

        for (int i = 0; i < numToRemove; i++)
        {
            _txReceipts.RemoveAt(_txReceipts.Count - 1);
        }

        Block.Header.GasUsed = _txReceipts.Count > 0 ? _txReceipts.Last().GasUsedTotal : 0;
    }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        _otherTracer.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block)
    {
        if (_otherTracer is null)
        {
            throw new InvalidOperationException("other tracer not set in receipts tracer");
        }

        Block = block;
        _currentIndex = 0;
        _txReceipts.Clear();

        _otherTracer.StartNewBlockTrace(block);
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

    public void EndBlockTrace()
    {
        _otherTracer.EndBlockTrace();
        if (_txReceipts.Count > 0)
        {
            Bloom blockBloom = new();
            Block.Header.Bloom = blockBloom;
            for (int index = 0; index < _txReceipts.Count; index++)
            {
                TxReceipt? receipt = _txReceipts[index];
                blockBloom.Accumulate(receipt.Bloom!);
            }
        }
    }

    public void SetOtherTracer(IBlockTracer blockTracer)
    {
        _otherTracer = blockTracer;
    }

    public void Dispose()
    {
        _currentTxTracer.Dispose();
    }
}
