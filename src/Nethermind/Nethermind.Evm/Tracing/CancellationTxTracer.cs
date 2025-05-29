// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public class CancellationTxTracer(ITxTracer innerTracer, CancellationToken token = default) : ITxTracer, ITxTracerWrapper
{
    private readonly bool _isTracingReceipt;
    private readonly bool _isTracingActions;
    private readonly bool _isTracingOpLevelStorage;
    private readonly bool _isTracingMemory;
    private readonly bool _isTracingInstructions;
    private readonly bool _isTracingRefunds;
    private readonly bool _isTracingCode;
    private readonly bool _isTracingStack;
    private readonly bool _isTracingState;
    private readonly bool _isTracingStorage;
    private readonly bool _isTracingBlockHash;
    private readonly bool _isTracingBlockAccess;
    private readonly bool _isTracingFees;
    private readonly bool _isTracingOpLevelLogs;

    public ITxTracer InnerTracer => innerTracer;

    public bool IsCancelable => true;
    public bool IsCancelled => token.IsCancellationRequested;

    public bool IsTracingReceipt
    {
        get => _isTracingReceipt || innerTracer.IsTracingReceipt;
        init => _isTracingReceipt = value;
    }

    public bool IsTracingActions
    {
        get => _isTracingActions || innerTracer.IsTracingActions;
        init => _isTracingActions = value;
    }

    public bool IsTracingOpLevelStorage
    {
        get => _isTracingOpLevelStorage || innerTracer.IsTracingOpLevelStorage;
        init => _isTracingOpLevelStorage = value;
    }

    public bool IsTracingMemory
    {
        get => _isTracingMemory || innerTracer.IsTracingMemory;
        init => _isTracingMemory = value;
    }

    public bool IsTracingInstructions
    {
        get => _isTracingInstructions || innerTracer.IsTracingInstructions;
        init => _isTracingInstructions = value;
    }

    public bool IsTracingRefunds
    {
        get => _isTracingRefunds || innerTracer.IsTracingRefunds;
        init => _isTracingRefunds = value;
    }

    public bool IsTracingCode
    {
        get => _isTracingCode || innerTracer.IsTracingCode;
        init => _isTracingCode = value;
    }

    public bool IsTracingStack
    {
        get => _isTracingStack || innerTracer.IsTracingStack;
        init => _isTracingStack = value;
    }

    public bool IsTracingState
    {
        get => _isTracingState || innerTracer.IsTracingState;
        init => _isTracingState = value;
    }

    public bool IsTracingStorage
    {
        get => _isTracingStorage || innerTracer.IsTracingStorage;
        init => _isTracingStorage = value;
    }

    public bool IsTracingBlockHash
    {
        get => _isTracingBlockHash || innerTracer.IsTracingBlockHash;
        init => _isTracingBlockHash = value;
    }

    public bool IsTracingAccess
    {
        get => _isTracingBlockAccess || innerTracer.IsTracingAccess;
        init => _isTracingBlockAccess = value;
    }

    public bool IsTracingFees
    {
        get => _isTracingFees || innerTracer.IsTracingFees;
        init => _isTracingFees = value;
    }

    public bool IsTracingLogs
    {
        get => _isTracingOpLevelLogs || innerTracer.IsTracingLogs;
        init => _isTracingOpLevelLogs = value;
    }


    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingState)
        {
            innerTracer.ReportBalanceChange(address, before, after);
        }
    }

    public void ReportCodeChange(Address address, byte[] before, byte[] after)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingState)
        {
            innerTracer.ReportCodeChange(address, before, after);
        }
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingState)
        {
            innerTracer.ReportNonceChange(address, before, after);
        }
    }

    public void ReportAccountRead(Address address)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingState)
        {
            innerTracer.ReportAccountRead(address);
        }
    }

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingStorage)
        {
            innerTracer.ReportStorageChange(storageCell, before, after);
        }
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingStorage)
        {
            innerTracer.ReportStorageRead(storageCell);
        }
    }

    public void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingReceipt)
        {
            innerTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        }
    }

    public void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingReceipt)
        {
            innerTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        }
    }

    public void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.StartOperation(pc, opcode, gas, env, codeSection, functionDepth);
        }
    }

    public void ReportOperationError(EvmExceptionType error)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportOperationError(error);
        }
    }

    public void ReportOperationRemainingGas(long gas)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportOperationRemainingGas(gas);
        }
    }

    public void ReportLog(LogEntry log)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingLogs)
        {
            innerTracer.ReportLog(log);
        }
    }

    public void SetOperationStack(TraceStack stack)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingStack)
        {
            innerTracer.SetOperationStack(stack);
        }
    }

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportStackPush(stackItem);
        }
    }

    public void ReportStackPush(in ZeroPaddedSpan stackItem)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportStackPush(stackItem);
        }
    }

    public void ReportStackPush(byte stackItem)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportStackPush(stackItem);
        }
    }

    public void SetOperationMemory(TraceMemory memoryTrace)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingMemory)
        {
            innerTracer.SetOperationMemory(memoryTrace);
        }
    }

    public void SetOperationMemorySize(ulong newSize)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingMemory)
        {
            innerTracer.SetOperationMemorySize(newSize);
        }
    }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportMemoryChange(offset, data);
        }
    }

    public void ReportMemoryChange(UInt256 offset, in ZeroPaddedSpan data)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportMemoryChange(offset, data);
        }
    }

    public void ReportMemoryChange(UInt256 offset, byte data)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportMemoryChange(offset, data);
        }
    }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportStorageChange(key, value);
        }
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingOpLevelStorage)
        {
            innerTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);
        }
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingOpLevelStorage)
        {
            innerTracer.LoadOperationStorage(address, storageIndex, value);
        }
    }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingActions)
        {
            innerTracer.ReportSelfDestruct(address, balance, refundAddress);
        }
    }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingActions)
        {
            innerTracer.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        }
    }

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingActions)
        {
            innerTracer.ReportActionEnd(gas, output);
        }
    }

    public void ReportActionError(EvmExceptionType evmExceptionType)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingActions)
        {
            innerTracer.ReportActionError(evmExceptionType);
        }
    }

    public void ReportActionRevert(long gasLeft, ReadOnlyMemory<byte> output)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingActions)
        {
            innerTracer.ReportActionRevert(gasLeft, output);
        }
    }

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingActions)
        {
            innerTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);
        }
    }

    public void ReportBlockHash(Hash256 blockHash)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingBlockHash)
        {
            innerTracer.ReportBlockHash(blockHash);
        }
    }

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingCode)
        {
            innerTracer.ReportByteCode(byteCode);
        }
    }

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingInstructions)
        {
            innerTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);
        }
    }

    public void ReportRefund(long refund)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingRefunds)
        {
            innerTracer.ReportRefund(refund);
        }
    }

    public void ReportExtraGasPressure(long extraGasPressure)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingRefunds)
        {
            innerTracer.ReportExtraGasPressure(extraGasPressure);
        }
    }

    public void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingAccess)
        {
            innerTracer.ReportAccess(accessedAddresses, accessedStorageCells);
        }
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        token.ThrowIfCancellationRequested();
        if (innerTracer.IsTracingFees)
        {
            innerTracer.ReportFees(fees, burntFees);
        }
    }

    public void Dispose()
    {
        innerTracer.Dispose();
    }
}
