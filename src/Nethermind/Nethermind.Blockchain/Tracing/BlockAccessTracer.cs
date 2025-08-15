// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public struct Access
{

}

public class BlockAccessTracer : IBlockTracer, ITxTracer, IJournal<int>
{
    // private IBlockTracer _otherTracer = NullBlockTracer.Instance;
    protected Block Block = null!;
    public bool IsTracingReceipt => false;
    public bool IsTracingActions => false;
    public bool IsTracingOpLevelStorage => false;
    public bool IsTracingMemory => false;
    public bool IsTracingInstructions => false;
    public bool IsTracingRefunds => false;
    public bool IsTracingCode => false;
    public bool IsTracingStack => false;
    public bool IsTracingState => true;
    public bool IsTracingStorage => true;

    public bool IsTracingBlockHash => false;
    public bool IsTracingAccess => true;
    public bool IsTracingFees => false;
    public bool IsTracingLogs => false;

    public void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        // _txReceipts.Add(BuildReceipt(recipient, gasSpent.SpentGas, StatusCode.Success, logs, stateRoot));
    }

    public void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        // _txReceipts.Add(BuildFailedReceipt(recipient, gasSpent.SpentGas, error, stateRoot));

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

    public void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0) {}

    public void ReportOperationError(EvmExceptionType error) {}


    public void ReportOperationRemainingGas(long gas) {}

    public void ReportLog(LogEntry log) {}

    public void SetOperationMemorySize(ulong newSize) {}

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data) {}

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        _accesses.Add(new());
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) {}

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) {}

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) {}

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        _accesses.Add(new());
    }

    public void ReportCodeChange(Address address, byte[] before, byte[] after)
    {
        _accesses.Add(new());
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        _accesses.Add(new());
    }

    public void ReportAccountRead(Address address)
    {
        _accesses.Add(new());
    }

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        _accesses.Add(new());
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        _accesses.Add(new());
    }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) {}

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output) {}

    public void ReportActionError(EvmExceptionType exceptionType) {}

    public void ReportActionRevert(long gasLeft, ReadOnlyMemory<byte> output) {}

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) {}

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode) {}

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable) {}

    public void ReportRefund(long refund) {}

    public void ReportExtraGasPressure(long extraGasPressure) {}

    public void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells)
    {
        _accesses.Add(new());
    }

    public void SetOperationStack(TraceStack stack) {}

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem) {}

    public void ReportBlockHash(Hash256 blockHash) {}

    public void SetOperationMemory(TraceMemory memoryTrace) {}

    public void ReportFees(UInt256 fees, UInt256 burntFees) {}

    // private ITxTracer _currentTxTracer = NullTxTracer.Instance;
    protected int _currentIndex { get; private set; }
    // private readonly List<TxReceipt> _txReceipts = new();
    private readonly List<Access> _accesses = [];
    protected Transaction? CurrentTx;
    public IReadOnlyList<Access> Accesses => _accesses;
    // public IReadOnlyList<TxReceipt> TxReceipts => _txReceipts;
    // public TxReceipt LastReceipt => _txReceipts[^1];
    public bool IsTracingRewards => false;

    // public ITxTracer InnerTracer => _currentTxTracer;

    public int TakeSnapshot() => _accesses.Count;

    public void Restore(int snapshot)
    {
        // int numToRemove = _txReceipts.Count - snapshot;

        // for (int i = 0; i < numToRemove; i++)
        // {
        //     _txReceipts.RemoveAt(_txReceipts.Count - 1);
        // }

        // Block.Header.GasUsed = _txReceipts.Count > 0 ? _txReceipts.Last().GasUsedTotal : 0;
    }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) {}

    public void StartNewBlockTrace(Block block)
    {
        Block = block;
        _currentIndex = 0;
        _accesses.Clear();
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        return this;
    }

    public void EndTxTrace()
    {
        _currentIndex++;
    }

    public void EndBlockTrace()
    {
        // _otherTracer.EndBlockTrace();
        // if (_txReceipts.Count > 0)
        // {
        //     Bloom blockBloom = new();
        //     Block.Header.Bloom = blockBloom;
        //     for (int index = 0; index < _txReceipts.Count; index++)
        //     {
        //         TxReceipt? receipt = _txReceipts[index];
        //         blockBloom.Accumulate(receipt.Bloom!);
        //     }
        // }
    }

    // public void SetOtherTracer(IBlockTracer blockTracer)
    // {
    //     _otherTracer = blockTracer;
    // }

    public void Dispose()
    {
        // _currentTxTracer.Dispose();
    }
}
