// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.DebugTrace;
internal class DebugTracer : ITxTracer
{
    public DebugTracer(GethTraceOptions traceOptions)
    {
        innerTracer = new(traceOptions);
    }

    private GethLikeTxTracer innerTracer;
    public volatile bool MoveNext = true;
    
    public bool IsLiveTrace => true;

    public bool IsTracingReceipt => ((ITxTracer)innerTracer).IsTracingReceipt;

    public bool IsTracingActions => ((ITxTracer)innerTracer).IsTracingActions;

    public bool IsTracingOpLevelStorage => ((ITxTracer)innerTracer).IsTracingOpLevelStorage;

    public bool IsTracingMemory => ((ITxTracer)innerTracer).IsTracingMemory;

    public bool IsTracingInstructions => ((ITxTracer)innerTracer).IsTracingInstructions;

    public bool IsTracingRefunds => ((ITxTracer)innerTracer).IsTracingRefunds;

    public bool IsTracingCode => ((ITxTracer)innerTracer).IsTracingCode;

    public bool IsTracingStack => ((ITxTracer)innerTracer).IsTracingStack;

    public bool IsTracingBlockHash => ((ITxTracer)innerTracer).IsTracingBlockHash;

    public bool IsTracingAccess => ((ITxTracer)innerTracer).IsTracingAccess;

    public bool IsTracingFees => ((ITxTracer)innerTracer).IsTracingFees;

    public bool IsTracingState => ((IStateTracer)innerTracer).IsTracingState;

    public bool IsTracingStorage => ((IStorageTracer)innerTracer).IsTracingStorage;

    private object _lock = new();
    private Dictionary<int, Func<EvmState, bool>> _breakPoints = new();
    public bool IsStepByStepModeOn { get; set; } = false;
    public EvmState CurrentState;
    public void Wait(EvmState evmState)
    {
        lock (_lock)
        {
            CurrentState = evmState;
        }
        if(IsStepByStepModeOn)
        {
            Lock();
        } else
        {
            CheckBreakPoint();
        }
        while (!MoveNext) ;
    }

    public bool CanReadState() => !MoveNext;
    public void SetBreakPoint(int programCounter, Func<EvmState, bool> condition = null)
        => _breakPoints.Add(programCounter, condition);
    public void CheckBreakPoint()
    {
        if (_breakPoints.ContainsKey(CurrentState.ProgramCounter))
        {
            Func<EvmState, bool> condition = _breakPoints[CurrentState.ProgramCounter];
            bool conditionResults = condition is null ? true : condition.Invoke(CurrentState);
            if(conditionResults)
            {
                Lock();
            }
        }
    }
    public void Lock()
    {
        MoveNext = false;
    }

    public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
    {
        ((ITxTracer)innerTracer).MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
    }

    public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
    {
        ((ITxTracer)innerTracer).MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
    }

    public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        ((ITxTracer)innerTracer).StartOperation(depth, gas, opcode, pc, isPostMerge);
    }

    public void ReportOperationError(EvmExceptionType error)
    {
        ((ITxTracer)innerTracer).ReportOperationError(error);
    }

    public void ReportOperationRemainingGas(long gas)
    {
        ((ITxTracer)innerTracer).ReportOperationRemainingGas(gas);
    }

    public void SetOperationStack(List<string> stackTrace)
    {
        ((ITxTracer)innerTracer).SetOperationStack(stackTrace);
    }

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
        ((ITxTracer)innerTracer).ReportStackPush(stackItem);
    }

    public void SetOperationMemory(List<string> memoryTrace)
    {
        ((ITxTracer)innerTracer).SetOperationMemory(memoryTrace);
    }

    public void SetOperationMemorySize(ulong newSize)
    {
        ((ITxTracer)innerTracer).SetOperationMemorySize(newSize);
    }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        ((ITxTracer)innerTracer).ReportMemoryChange(offset, data);
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        ((ITxTracer)innerTracer).SetOperationStorage(address, storageIndex, newValue, currentValue);
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        ((ITxTracer)innerTracer).LoadOperationStorage(address, storageIndex, value);
    }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        ((ITxTracer)innerTracer).ReportSelfDestruct(address, balance, refundAddress);
    }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        ((ITxTracer)innerTracer).ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
    }

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        ((ITxTracer)innerTracer).ReportActionEnd(gas, output);
    }

    public void ReportActionError(EvmExceptionType evmExceptionType)
    {
        ((ITxTracer)innerTracer).ReportActionError(evmExceptionType);
    }

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        ((ITxTracer)innerTracer).ReportActionEnd(gas, deploymentAddress, deployedCode);
    }

    public void ReportBlockHash(Keccak blockHash)
    {
        ((ITxTracer)innerTracer).ReportBlockHash(blockHash);
    }

    public void ReportByteCode(byte[] byteCode)
    {
        ((ITxTracer)innerTracer).ReportByteCode(byteCode);
    }

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
    {
        ((ITxTracer)innerTracer).ReportGasUpdateForVmTrace(refund, gasAvailable);
    }

    public void ReportRefund(long refund)
    {
        ((ITxTracer)innerTracer).ReportRefund(refund);
    }

    public void ReportExtraGasPressure(long extraGasPressure)
    {
        ((ITxTracer)innerTracer).ReportExtraGasPressure(extraGasPressure);
    }

    public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
    {
        ((ITxTracer)innerTracer).ReportAccess(accessedAddresses, accessedStorageCells);
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        ((ITxTracer)innerTracer).ReportFees(fees, burntFees);
    }

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        ((IStateTracer)innerTracer).ReportBalanceChange(address, before, after);
    }

    public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
    {
        ((IStateTracer)innerTracer).ReportCodeChange(address, before, after);
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        ((IStateTracer)innerTracer).ReportNonceChange(address, before, after);
    }

    public void ReportAccountRead(Address address)
    {
        ((IStateTracer)innerTracer).ReportAccountRead(address);
    }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        ((IStorageTracer)innerTracer).ReportStorageChange(key, value);
    }

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        ((IStorageTracer)innerTracer).ReportStorageChange(storageCell, before, after);
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        ((IStorageTracer)innerTracer).ReportStorageRead(storageCell);
    }
}
