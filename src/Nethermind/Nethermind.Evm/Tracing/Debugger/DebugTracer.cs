// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if DEBUG
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
using Nethermind.State.Tracing;

namespace Nethermind.Evm.Tracing.DebugTrace;
public class DebugTracer : ITxTracer, ITxTracerWrapper, IDisposable
{
    public enum DebugPhase
    {
        Starting, Blocked, Running, Aborted
    }

    public DebugTracer(ITxTracer tracer)
    {
        InnerTracer = tracer;
    }

    public ITxTracer InnerTracer { get; private set; }
    public DebugPhase CurrentPhase { get; private set; } = DebugPhase.Starting;
    public bool CanReadState => CurrentPhase is DebugPhase.Blocked;

    private AutoResetEvent _autoResetEvent = new AutoResetEvent(false);

    public event Action BreakPointReached;
    public event Action ExecutionThreadSet;


    public bool IsTracingReceipt => ((ITxTracer)InnerTracer).IsTracingReceipt;

    public bool IsTracingActions => ((ITxTracer)InnerTracer).IsTracingActions;

    public bool IsTracingOpLevelStorage => ((ITxTracer)InnerTracer).IsTracingOpLevelStorage;

    public bool IsTracingMemory => ((ITxTracer)InnerTracer).IsTracingMemory;

    public bool IsTracingInstructions => ((ITxTracer)InnerTracer).IsTracingInstructions;

    public bool IsTracingRefunds => ((ITxTracer)InnerTracer).IsTracingRefunds;

    public bool IsTracingCode => ((ITxTracer)InnerTracer).IsTracingCode;

    public bool IsTracingStack => ((ITxTracer)InnerTracer).IsTracingStack;

    public bool IsTracingBlockHash => ((ITxTracer)InnerTracer).IsTracingBlockHash;

    public bool IsTracingAccess => ((ITxTracer)InnerTracer).IsTracingAccess;

    public bool IsTracingFees => ((ITxTracer)InnerTracer).IsTracingFees;

    public bool IsTracingState => ((IStateTracer)InnerTracer).IsTracingState;

    public bool IsTracingStorage => ((IStorageTracer)InnerTracer).IsTracingStorage;


    internal Dictionary<(int depth, int pc), Func<EvmState, bool>> _breakPoints = new();
    public bool IsBreakpoitnSet(int depth, int programCounter)
        => _breakPoints.ContainsKey((depth, programCounter));
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
    private Func<EvmState, bool> _globalBreakCondition = null;
    public void SetCondtion(Func<EvmState, bool> condition = null)
    {
        if (CurrentPhase is DebugPhase.Blocked or DebugPhase.Starting) _globalBreakCondition = condition;
    }

    private object _lock = new();
    public bool IsStepByStepModeOn { get; set; } = false;
    public EvmState CurrentState;
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
        var breakpoint = (CurrentState.Env.CallDepth, CurrentState.ProgramCounter);
        if (_breakPoints.ContainsKey(breakpoint))
        {
            Func<EvmState, bool> condition = _breakPoints[breakpoint];
            bool conditionResults = condition is null ? true : condition.Invoke(CurrentState);
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
    {
        ((ITxTracer)InnerTracer).MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
    }

    public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
    {
        ((ITxTracer)InnerTracer).MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
    }

    public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        ((ITxTracer)InnerTracer).StartOperation(depth, gas, opcode, pc, isPostMerge);
    }

    public void ReportOperationError(EvmExceptionType error)
    {
        ((ITxTracer)InnerTracer).ReportOperationError(error);
    }

    public void ReportOperationRemainingGas(long gas)
    {
        ((ITxTracer)InnerTracer).ReportOperationRemainingGas(gas);
    }

    public void SetOperationStack(List<string> stackTrace)
    {
        ((ITxTracer)InnerTracer).SetOperationStack(stackTrace);
    }

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
        ((ITxTracer)InnerTracer).ReportStackPush(stackItem);
    }

    public void SetOperationMemory(List<string> memoryTrace)
    {
        ((ITxTracer)InnerTracer).SetOperationMemory(memoryTrace);
    }

    public void SetOperationMemorySize(ulong newSize)
    {
        ((ITxTracer)InnerTracer).SetOperationMemorySize(newSize);
    }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        ((ITxTracer)InnerTracer).ReportMemoryChange(offset, data);
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        ((ITxTracer)InnerTracer).SetOperationStorage(address, storageIndex, newValue, currentValue);
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        ((ITxTracer)InnerTracer).LoadOperationStorage(address, storageIndex, value);
    }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        ((ITxTracer)InnerTracer).ReportSelfDestruct(address, balance, refundAddress);
    }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        ((ITxTracer)InnerTracer).ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
    }

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        ((ITxTracer)InnerTracer).ReportActionEnd(gas, output);
    }

    public void ReportActionError(EvmExceptionType evmExceptionType)
    {
        ((ITxTracer)InnerTracer).ReportActionError(evmExceptionType);
    }

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        ((ITxTracer)InnerTracer).ReportActionEnd(gas, deploymentAddress, deployedCode);
    }

    public void ReportBlockHash(Keccak blockHash)
    {
        ((ITxTracer)InnerTracer).ReportBlockHash(blockHash);
    }

    public void ReportByteCode(byte[] byteCode)
    {
        ((ITxTracer)InnerTracer).ReportByteCode(byteCode);
    }

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
    {
        ((ITxTracer)InnerTracer).ReportGasUpdateForVmTrace(refund, gasAvailable);
    }

    public void ReportRefund(long refund)
    {
        ((ITxTracer)InnerTracer).ReportRefund(refund);
    }

    public void ReportExtraGasPressure(long extraGasPressure)
    {
        ((ITxTracer)InnerTracer).ReportExtraGasPressure(extraGasPressure);
    }

    public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
    {
        ((ITxTracer)InnerTracer).ReportAccess(accessedAddresses, accessedStorageCells);
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        ((ITxTracer)InnerTracer).ReportFees(fees, burntFees);
    }

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        ((IStateTracer)InnerTracer).ReportBalanceChange(address, before, after);
    }

    public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
    {
        ((IStateTracer)InnerTracer).ReportCodeChange(address, before, after);
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        ((IStateTracer)InnerTracer).ReportNonceChange(address, before, after);
    }

    public void ReportAccountRead(Address address)
    {
        ((IStateTracer)InnerTracer).ReportAccountRead(address);
    }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        ((IStorageTracer)InnerTracer).ReportStorageChange(key, value);
    }

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        ((IStorageTracer)InnerTracer).ReportStorageChange(storageCell, before, after);
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        ((IStorageTracer)InnerTracer).ReportStorageRead(storageCell);
    }

    public void Dispose()
    {
        _autoResetEvent?.Dispose();
    }
}
#endif
