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

    public void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0) {}

    public void ReportOperationError(EvmExceptionType error) {}


    public void ReportOperationRemainingGas(long gas) {}

    public void ReportLog(LogEntry log) {}

    public void SetOperationMemorySize(ulong newSize) {}

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data) {}

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        //_bal.AccountChanges[].StorageChanges()
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) {}

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) {}

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) {}

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        BalanceChange balanceChange = new()
        {
            TxIndex = (ushort)_currentIndex,
            PostBalance = (ulong)after // why not 256 bit?
        };
        _bal.AccountChanges[address].BalanceChanges.Add(balanceChange);
    }

    public void ReportCodeChange(Address address, byte[] before, byte[] after)
    {
        CodeChange codeChange = new()
        {
            TxIndex = (ushort)_currentIndex,
            NewCode = after
        };
        _bal.AccountChanges[address].CodeChanges.Add(codeChange);
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        NonceChange nonceChange = new()
        {
            TxIndex = (ushort)_currentIndex,
            NewNonce = (ulong)after
        };
        _bal.AccountChanges[address].NonceChanges.Add(nonceChange);
    }

    public void ReportAccountRead(Address address)
    {
        if (!_bal.AccountChanges.ContainsKey(address))
        {
            _bal.AccountChanges.Add(address, new());
        }
    }

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        StorageChange storageChange = new()
        {
            TxIndex = (ushort)_currentIndex,
            NewValue = after
        };
        Address address = Address.Zero;
        _bal.AccountChanges[address].StorageChanges.Add(storageChange);
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        StorageKey storageKey = new()
        {
            Key = storageCell.Hash.ToByteArray()
        };
        Address address = Address.Zero;
        _bal.AccountChanges[address].StorageReads.Add(storageKey);
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
        // _bal.Add(new());
    }

    public void SetOperationStack(TraceStack stack) {}

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem) {}

    public void ReportBlockHash(Hash256 blockHash) {}

    public void SetOperationMemory(TraceMemory memoryTrace) {}

    public void ReportFees(UInt256 fees, UInt256 burntFees) {}

    // private ITxTracer _currentTxTracer = NullTxTracer.Instance;
    protected int _currentIndex { get; private set; }
    // private readonly List<TxReceipt> _txReceipts = new();
    private BlockAccessList _bal = new();
    protected Transaction? CurrentTx;
    public BlockAccessList BlockAccessList => _bal;
    // public TxReceipt LastReceipt => _txReceipts[^1];
    public bool IsTracingRewards => false;

    // public ITxTracer InnerTracer => _currentTxTracer;

    public int TakeSnapshot() => _bal.AccountChanges.Count;

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
        _bal = new();
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
