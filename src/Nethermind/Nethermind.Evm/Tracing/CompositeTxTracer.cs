// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public class CompositeTxTracer : ITxTracer
{
    internal readonly IList<ITxTracer> _txTracers;

    public CompositeTxTracer(params ITxTracer[] txTracers) : this((IList<ITxTracer>)txTracers)
    {
    }

    public CompositeTxTracer(IList<ITxTracer> txTracers)
    {
        _txTracers = txTracers;
        for (int index = 0; index < txTracers.Count; index++)
        {
            ITxTracer t = txTracers[index];
            IsTracingState |= t.IsTracingState;
            IsTracingReceipt |= t.IsTracingReceipt;
            IsTracingActions |= t.IsTracingActions;
            IsTracingOpLevelStorage |= t.IsTracingOpLevelStorage;
            IsTracingMemory |= t.IsTracingMemory;
            IsTracingInstructions |= t.IsTracingInstructions;
            IsTracingRefunds |= t.IsTracingRefunds;
            IsTracingCode |= t.IsTracingCode;
            IsTracingStack |= t.IsTracingStack;
            IsTracingBlockHash |= t.IsTracingBlockHash;
            IsTracingStorage |= t.IsTracingStorage;
            IsTracingAccess |= t.IsTracingAccess;
            IsTracingFees |= t.IsTracingFees;
        }
    }

    public bool IsTracingState { get; }
    public bool IsTracingStorage { get; }
    public bool IsTracingReceipt { get; }
    public bool IsTracingActions { get; }
    public bool IsTracingOpLevelStorage { get; }
    public bool IsTracingMemory { get; }
    public bool IsTracingInstructions { get; }
    public bool IsTracingRefunds { get; }
    public bool IsTracingCode { get; }
    public bool IsTracingStack { get; }
    public bool IsTracingBlockHash { get; }
    public bool IsTracingAccess { get; }
    public bool IsTracingFees { get; }
    public bool IsTracingLogs { get; }

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingState)
            {
                innerTracer.ReportBalanceChange(address, before, after);
            }
        }
    }

    public void ReportCodeChange(Address address, byte[] before, byte[] after)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingState)
            {
                innerTracer.ReportCodeChange(address, before, after);
            }
        }
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingState)
            {
                innerTracer.ReportNonceChange(address, before, after);
            }
        }
    }

    public void ReportAccountRead(Address address)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingState)
            {
                innerTracer.ReportAccountRead(address);
            }
        }
    }

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingStorage)
            {
                innerTracer.ReportStorageChange(storageCell, before, after);
            }
        }
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingStorage)
            {
                innerTracer.ReportStorageRead(storageCell);
            }
        }
    }

    public void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingReceipt)
            {
                innerTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
            }
        }
    }

    public void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingReceipt)
            {
                innerTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
            }
        }
    }

    public void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.StartOperation(pc, opcode, gas, env, codeSection, functionDepth);
            }
        }
    }

    public void ReportOperationError(EvmExceptionType error)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportOperationError(error);
            }
        }
    }

    public void ReportOperationRemainingGas(long gas)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportOperationRemainingGas(gas);
            }
        }
    }

    public void ReportOperationLogs(LogEntry log)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions && innerTracer.IsTracingLogs)
            {
                innerTracer.ReportLog(log);
            }
        }
    }

    public void ReportLog(LogEntry log)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportLog(log);
            }
        }
    }

    public void SetOperationStack(TraceStack stack)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingStack)
            {
                innerTracer.SetOperationStack(stack);
            }
        }
    }

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportStackPush(stackItem);
            }
        }
    }

    public void ReportStackPush(in ZeroPaddedSpan stackItem)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportStackPush(stackItem);
            }
        }
    }

    public void ReportStackPush(byte stackItem)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportStackPush(stackItem);
            }
        }
    }

    public void SetOperationMemory(TraceMemory memoryTrace)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingMemory)
            {
                innerTracer.SetOperationMemory(memoryTrace);
            }
        }
    }

    public void SetOperationMemorySize(ulong newSize)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingMemory)
            {
                innerTracer.SetOperationMemorySize(newSize);
            }
        }
    }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportMemoryChange(offset, data);
            }
        }
    }

    public void ReportMemoryChange(UInt256 offset, in ZeroPaddedSpan data)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportMemoryChange(offset, data);
            }
        }
    }

    public void ReportMemoryChange(UInt256 offset, byte data)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportMemoryChange(offset, data);
            }
        }
    }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportStorageChange(key, value);
            }
        }
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingOpLevelStorage)
            {
                innerTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);
            }
        }
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingOpLevelStorage)
            {
                innerTracer.LoadOperationStorage(address, storageIndex, value);
            }
        }
    }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingActions)
            {
                innerTracer.ReportSelfDestruct(address, balance, refundAddress);
            }
        }
    }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingActions)
            {
                innerTracer.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
            }
        }
    }

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingActions)
            {
                innerTracer.ReportActionEnd(gas, output);
            }
        }
    }

    public void ReportActionError(EvmExceptionType evmExceptionType)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingActions)
            {
                innerTracer.ReportActionError(evmExceptionType);
            }
        }
    }

    public void ReportActionRevert(long gasLeft, ReadOnlyMemory<byte> output)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingActions)
            {
                innerTracer.ReportActionRevert(gasLeft, output);
            }
        }
    }

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingActions)
            {
                innerTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);
            }
        }
    }

    public void ReportBlockHash(Hash256 blockHash)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingBlockHash)
            {
                innerTracer.ReportBlockHash(blockHash);
            }
        }
    }

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingCode)
            {
                innerTracer.ReportByteCode(byteCode);
            }
        }
    }

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingInstructions)
            {
                innerTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);
            }
        }
    }

    public void ReportRefund(long refund)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingRefunds)
            {
                innerTracer.ReportRefund(refund);
            }
        }
    }

    public void ReportExtraGasPressure(long extraGasPressure)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingRefunds)
            {
                innerTracer.ReportExtraGasPressure(extraGasPressure);
            }
        }
    }

    public void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingAccess)
            {
                innerTracer.ReportAccess(accessedAddresses, accessedStorageCells);
            }
        }
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            ITxTracer innerTracer = _txTracers[index];
            if (innerTracer.IsTracingFees)
            {
                innerTracer.ReportFees(fees, burntFees);
            }
        }
    }

    public void Dispose()
    {
        for (int index = 0; index < _txTracers.Count; index++)
        {
            _txTracers[index].Dispose();
        }
    }
}
