// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Test.Runner;

public class StateTestTxTracer : ITxTracer, IDisposable
{
    private StateTestTxTraceEntry _traceEntry;
    private StateTestTxTrace _trace = new();
    private bool _gasAlreadySetForCurrentOp;

    public bool IsTracingReceipt => true;
    public bool IsTracingActions => false;
    public bool IsTracingOpLevelStorage => true;
    public bool IsTracingMemory => true;
    public bool IsTracingDetailedMemory { get; set; } = true;
    public bool IsTracingInstructions => true;
    public bool IsTracingRefunds { get; } = false;
    public bool IsTracingCode => false;
    public bool IsTracingStack { get; set; } = true;
    public bool IsTracingState => false;
    public bool IsTracingStorage => false;
    public bool IsTracingBlockHash { get; } = false;
    public bool IsTracingAccess { get; } = false;
    public bool IsTracingFees => false;
    public bool IsTracingLogs => false;
    public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees || IsTracingLogs;


    public void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256 stateRoot = null)
    {
        _trace.Result.Output = output;
        _trace.Result.GasUsed = gasSpent;
    }

    public void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string error, Hash256 stateRoot = null)
    {
        _trace.Result.Error = _traceEntry?.Error ?? error;
        _trace.Result.Output = output ?? Bytes.Empty;
        _trace.Result.GasUsed = gasSpent;
    }

    public void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
    {
        bool isPostMerge = env.IsPostMerge();
        _gasAlreadySetForCurrentOp = false;
        _traceEntry = new StateTestTxTraceEntry();
        _traceEntry.Pc = pc + env.CodeInfo.PcOffset();
        _traceEntry.Section = codeSection;
        _traceEntry.Operation = (byte)opcode;
        _traceEntry.OperationName = opcode.GetName(isPostMerge);
        _traceEntry.Gas = gas;
        _traceEntry.Depth = env.GetGethTraceDepth();
        _traceEntry.FunctionDepth = functionDepth;
        _trace.Entries.Add(_traceEntry);
    }

    public void ReportOperationError(EvmExceptionType error)
    {
        if (_traceEntry is null) return;

        _traceEntry.Error = GetErrorDescription(error);
    }

    private static string? GetErrorDescription(EvmExceptionType evmExceptionType)
    {
        return evmExceptionType switch
        {
            EvmExceptionType.None => null,
            EvmExceptionType.BadInstruction => "BadInstruction",
            EvmExceptionType.StackOverflow => "StackOverflow",
            EvmExceptionType.StackUnderflow => "StackUnderflow",
            EvmExceptionType.OutOfGas => "OutOfGas",
            EvmExceptionType.InvalidJumpDestination => "BadJumpDestination",
            EvmExceptionType.AccessViolation => "AccessViolation",
            EvmExceptionType.StaticCallViolation => "StaticCallViolation",
            _ => "Error"
        };
    }

    public void ReportOperationRemainingGas(long gas)
    {
        if (_traceEntry is null) return;

        if (!_gasAlreadySetForCurrentOp)
        {
            _gasAlreadySetForCurrentOp = true;
            _traceEntry.GasCost = _traceEntry.Gas - gas;
        }
    }

    public void SetOperationStack(TraceStack stack)
    {
        _traceEntry.Stack = [];
        foreach (string s in stack.ToHexWordList())
        {
            ReadOnlySpan<char> inProgress = s.AsSpan();
            if (s.StartsWith("0x"))
                inProgress = inProgress.Slice(2);

            inProgress = inProgress.TrimStart('0');

            _traceEntry.Stack.Add(inProgress.Length == 0 ? "0x0" : "0x" + inProgress.ToString());
        }

    }

    public void SetOperationMemory(TraceMemory memoryTrace)
    {
        if (IsTracingDetailedMemory)
        {
            var length = 2;
            var wordList = memoryTrace.ToHexWordList();
            for (int i = 0; i < wordList.Length; i++)
            {
                length += wordList[i].Length;
            }

            _traceEntry.Memory = string.Create(length, wordList, static (span, words) =>
            {
                span[1] = 'x';
                span[0] = '0';

                span = span[2..];
                for (int i = 0; i < words.Length; i++)
                {
                    ReadOnlySpan<char> word = words[i].AsSpan();
                    word.CopyTo(span);
                    span = span[word.Length..];
                }
            });
        }
    }

    public void SetOperationMemorySize(ulong newSize)
    {
        _traceEntry.UpdateMemorySize((int)newSize);
        if (IsTracingDetailedMemory)
        {
            int diff = _traceEntry.MemSize * 2 - (_traceEntry.Memory.Length - 2);
            if (diff > 0)
                _traceEntry.Memory += new string('0', diff);
        }
    }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
    }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
    }

    public void ReportLog(LogEntry log)
    {
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
    }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        throw new NotSupportedException();
    }

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        throw new NotSupportedException();
    }

    public void ReportCodeChange(Address address, byte[] before, byte[] after)
    {
        throw new NotSupportedException();
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        throw new NotSupportedException();
    }

    public void ReportAccountRead(Address address)
    {
        throw new NotImplementedException();
    }

    public void ReportStorageChange(in StorageCell storageAddress, byte[] before, byte[] after)
    {
        throw new NotSupportedException();
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        throw new NotSupportedException();
    }

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        throw new NotSupportedException();
    }

    public void ReportActionError(EvmExceptionType exceptionType)
    {
        throw new NotSupportedException();
    }

    public void ReportActionRevert(long gas, ReadOnlyMemory<byte> output)
    {
        throw new NotSupportedException();
    }

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        throw new NotSupportedException();
    }

    public void ReportBlockHash(Hash256 blockHash)
    {
        throw new NotImplementedException();
    }

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode)
    {
        throw new NotSupportedException();
    }

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
    {
    }

    public void ReportRefundForVmTrace(long refund, long gasAvailable)
    {
    }

    public void ReportRefund(long refund)
    {
        _traceEntry.Refund = (int)refund;
    }

    public void ReportExtraGasPressure(long extraGasPressure)
    {
        throw new NotImplementedException();
    }

    public void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells)
    {
        throw new NotImplementedException();
    }

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
    }

    public StateTestTxTrace BuildResult()
    {
        return _trace;
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        throw new NotImplementedException();
    }

    public void Dispose() { }
}
