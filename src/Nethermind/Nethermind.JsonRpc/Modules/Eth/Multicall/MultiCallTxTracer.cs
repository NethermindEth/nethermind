// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.Multicall;

//Why this is not visitor like DDD...
internal class MultiCallTxTracer : ITxTracer
{
    private readonly List<LogEntry> _extendedLogs;

    public MultiCallTxTracer(Transaction t, List<LogEntry> extendedLogs)
    {
        _extendedLogs = extendedLogs;
    }

    public bool IsTracingState { get; }

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        throw new NotImplementedException();
    }

    public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
    {
        throw new NotImplementedException();
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        throw new NotImplementedException();
    }

    public void ReportAccountRead(Address address)
    {
        throw new NotImplementedException();
    }

    public bool IsTracingStorage { get; }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        throw new NotImplementedException();
    }

    public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        throw new NotImplementedException();
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public bool IsTracingReceipt { get; }
    public bool IsTracingActions => true;
    public bool IsTracingOpLevelStorage { get; }
    public bool IsTracingMemory { get; }
    public bool IsTracingInstructions { get; }
    public bool IsTracingRefunds { get; }
    public bool IsTracingCode { get; }
    public bool IsTracingStack { get; }
    public bool IsTracingBlockHash { get; }
    public bool IsTracingAccess { get; }
    public bool IsTracingFees { get; }


    public bool IsTracing => IsTracingActions || IsTracingEventLogs;
    public bool IsTracingEventLogs => true;

    public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs,
        Keccak? stateRoot = null)
    {
        throw new NotImplementedException();
    }

    public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
    {
        throw new NotImplementedException();
    }

    public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        throw new NotImplementedException();
    }

    public void ReportOperationError(EvmExceptionType error)
    {
        throw new NotImplementedException();
    }

    public void ReportOperationRemainingGas(long gas)
    {
        throw new NotImplementedException();
    }

    public void SetOperationStack(List<string> stackTrace)
    {
        throw new NotImplementedException();
    }

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
        throw new NotImplementedException();
    }

    public void SetOperationMemory(List<string> memoryTrace)
    {
        throw new NotImplementedException();
    }

    public void SetOperationMemorySize(ulong newSize)
    {
        throw new NotImplementedException();
    }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        throw new NotImplementedException();
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue,
        ReadOnlySpan<byte> currentValue)
    {
        throw new NotImplementedException();
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        throw new NotImplementedException();
    }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
    }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType,
        bool isPrecompileCall = false)
    {
        byte[]? data = AbiEncoder.Instance.Encode(AbiEncodingStyle.Packed,
            new AbiSignature("Transfer", AbiType.Address, AbiType.Address, AbiType.UInt256), from, to, value);
        LogEntry? result = new(Address.Zero, data, new[] { Keccak.Zero });
        _extendedLogs.Add(result);
    }

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
    }

    public void ReportActionError(EvmExceptionType evmExceptionType)
    {
    }

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        throw new NotImplementedException();
    }

    public void ReportBlockHash(Keccak blockHash)
    {
        throw new NotImplementedException();
    }

    public void ReportByteCode(byte[] byteCode)
    {
        throw new NotImplementedException();
    }

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
    {
        throw new NotImplementedException();
    }

    public void ReportRefund(long refund)
    {
        throw new NotImplementedException();
    }

    public void ReportExtraGasPressure(long extraGasPressure)
    {
        throw new NotImplementedException();
    }

    public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
    {
        throw new NotImplementedException();
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        throw new NotImplementedException();
    }

    public void ReportEvent(LogEntry logEntry)
    {
        _extendedLogs.Add(logEntry);
    }
}
