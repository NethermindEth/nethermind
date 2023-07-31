// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if DEBUG
using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.Debugger;
public class DebugTracer : ITxTracer, ITxTracerWrapper, IDisposable
{
    public enum DebugPhase { Starting, Blocked, Running, Aborted }

    private readonly AutoResetEvent _autoResetEvent = new(false);
    private readonly Dictionary<(int depth, int pc), Func<EvmState, bool>> _breakPoints = new();
    private Func<EvmState, bool>? _globalBreakCondition;
    private readonly object _lock = new();

    public DebugTracer(ITxTracer tracer)
    {
        InnerTracer = tracer;
    }

    public event Action? BreakPointReached;
    public event Action? ExecutionThreadSet;
    public ITxTracer InnerTracer { get; private set; }
    public DebugPhase CurrentPhase { get; private set; } = DebugPhase.Starting;
    public bool CanReadState => CurrentPhase is DebugPhase.Blocked;
    public bool IsStepByStepModeOn { get; set; }
    public EvmState? CurrentState { get; set; }

    public bool IsTracingReceipt => InnerTracer.IsTracingReceipt;

    public bool IsTracingActions => InnerTracer.IsTracingActions;

    public bool IsTracingOpLevelStorage => InnerTracer.IsTracingOpLevelStorage;

    public bool IsTracingMemory => InnerTracer.IsTracingMemory;

    public bool IsTracingInstructions => InnerTracer.IsTracingInstructions;

    public bool IsTracingRefunds => InnerTracer.IsTracingRefunds;

    public bool IsTracingCode => InnerTracer.IsTracingCode;

    public bool IsTracingStack => InnerTracer.IsTracingStack;

    public bool IsTracingBlockHash => InnerTracer.IsTracingBlockHash;

    public bool IsTracingAccess => InnerTracer.IsTracingAccess;

    public bool IsTracingFees => InnerTracer.IsTracingFees;

    public bool IsTracingState => InnerTracer.IsTracingState;

    public bool IsTracingStorage => InnerTracer.IsTracingStorage;

    public bool IsBreakpoitnSet(int depth, int programCounter) => _breakPoints.ContainsKey((depth, programCounter));

    public void SetBreakPoint((int depth, int pc) point, Func<EvmState, bool> condition = null)
    {
        if (CurrentPhase is DebugPhase.Blocked or DebugPhase.Starting)
        {
            _breakPoints[point] = condition;
        }
    }
    public void UnsetBreakPoint(int depth, int programCounter)
    {
        if (CurrentPhase is DebugPhase.Blocked or DebugPhase.Starting)
        {
            _breakPoints.Remove((depth, programCounter));
        }
    }

    public void SetCondtion(Func<EvmState, bool>? condition = null)
    {
        if (CurrentPhase is DebugPhase.Blocked or DebugPhase.Starting) _globalBreakCondition = condition;
    }

    public void TryWait(ref EvmState evmState, ref int programCounter, ref long gasAvailable, ref int stackHead)
    {
        if (CurrentPhase is DebugPhase.Aborted)
        {
            return;
        }

        lock (_lock)
        {
            evmState.ProgramCounter = programCounter;
            evmState.GasAvailable = gasAvailable;
            evmState.DataStackHead = stackHead;
            CurrentState = evmState;
        }

        if (IsStepByStepModeOn)
        {
            Block();
        }
        else
        {
            CheckBreakPoint();
        }


        lock (_lock)
        {
            stackHead = CurrentState.DataStackHead;
            gasAvailable = CurrentState.GasAvailable;
            programCounter = CurrentState.ProgramCounter;
        }
    }

    public void Reset(ITxTracer newInnerTracer)
    {
        lock (_lock)
        {
            CurrentPhase = DebugPhase.Starting;
            _breakPoints.Clear();
            CurrentState = null;
            InnerTracer = newInnerTracer;
        }
        _autoResetEvent.Reset();
    }

    private void Block()
    {
        lock (_lock)
        {
            CurrentPhase = DebugPhase.Blocked;
            BreakPointReached?.Invoke();
        }
        _autoResetEvent.WaitOne();
    }

    public void Abort()
    {
        lock (_lock)
        {
            CurrentPhase = DebugPhase.Aborted;
        }
        _autoResetEvent.Set();
    }

    public void MoveNext(bool? executeOneStep = null)
    {
        lock (_lock)
        {
            IsStepByStepModeOn = executeOneStep ?? IsStepByStepModeOn;
            CurrentPhase = DebugPhase.Running;
            ExecutionThreadSet?.Invoke();
        }
        _autoResetEvent.Set();
    }

    public void CheckBreakPoint()
    {
        (int CallDepth, int ProgramCounter) breakpoint = (CurrentState!.Env.CallDepth, CurrentState.ProgramCounter);

        if (_breakPoints.TryGetValue(breakpoint, out Func<EvmState, bool>? point))
        {
            bool conditionResults = point?.Invoke(CurrentState) ?? true;
            if (conditionResults)
            {
                Block();
            }
        }
        else
        {
            if (_globalBreakCondition?.Invoke(CurrentState) ?? false)
            {
                Block();
            }
            else
            {
                lock (_lock)
                {
                    CurrentPhase = DebugPhase.Running;
                }
            }

        }
    }

    public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        => InnerTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);

    public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        => InnerTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);

    public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
        => InnerTracer.StartOperation(depth, gas, opcode, pc, isPostMerge);

    public void ReportOperationError(EvmExceptionType error)
        => InnerTracer.ReportOperationError(error);

    public void ReportOperationRemainingGas(long gas)
        => InnerTracer.ReportOperationRemainingGas(gas);

    public void SetOperationStack(List<string> stackTrace)
        => InnerTracer.SetOperationStack(stackTrace);

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        => InnerTracer.ReportStackPush(stackItem);

    public void SetOperationMemory(List<string> memoryTrace)
        => InnerTracer.SetOperationMemory(memoryTrace);

    public void SetOperationMemorySize(ulong newSize)
        => InnerTracer.SetOperationMemorySize(newSize);

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        => InnerTracer.ReportMemoryChange(offset, data);

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        => InnerTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        => InnerTracer.LoadOperationStorage(address, storageIndex, value);

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        => InnerTracer.ReportSelfDestruct(address, balance, refundAddress);

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        => InnerTracer.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        => InnerTracer.ReportActionEnd(gas, output);

    public void ReportActionError(EvmExceptionType evmExceptionType)
        => InnerTracer.ReportActionError(evmExceptionType);

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        => InnerTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);

    public void ReportBlockHash(Keccak blockHash)
        => InnerTracer.ReportBlockHash(blockHash);

    public void ReportByteCode(byte[] byteCode)
        => InnerTracer.ReportByteCode(byteCode);

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        => InnerTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);

    public void ReportRefund(long refund)
        => InnerTracer.ReportRefund(refund);

    public void ReportExtraGasPressure(long extraGasPressure)
        => InnerTracer.ReportExtraGasPressure(extraGasPressure);

    public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        => InnerTracer.ReportAccess(accessedAddresses, accessedStorageCells);

    public void ReportFees(UInt256 fees, UInt256 burntFees)
        => InnerTracer.ReportFees(fees, burntFees);

    public void ReportEvent(LogEntry logEntry)
    {
        ((ITxTracer)InnerTracer).ReportEvent(logEntry);
    }

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        => InnerTracer.ReportBalanceChange(address, before, after);

    public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
        => InnerTracer.ReportCodeChange(address, before, after);

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        => InnerTracer.ReportNonceChange(address, before, after);

    public void ReportAccountRead(Address address)
        => InnerTracer.ReportAccountRead(address);

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        => InnerTracer.ReportStorageChange(key, value);

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        => InnerTracer.ReportStorageChange(storageCell, before, after);

    public void ReportStorageRead(in StorageCell storageCell)
        => InnerTracer.ReportStorageRead(storageCell);

    public void Dispose()
    {
        _autoResetEvent.Dispose();
    }
}
#endif
