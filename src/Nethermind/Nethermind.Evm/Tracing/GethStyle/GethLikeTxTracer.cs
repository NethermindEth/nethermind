// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Tracing;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public class GethLikeTxTracer : ITxTracer
    {
        private GethTxTraceEntry? _traceEntry;
        private readonly GethLikeTxTrace _trace = new();

        public GethLikeTxTracer(GethTraceOptions options)
        {
            IsTracingStack = !options.DisableStack;
            IsTracingMemory = !options.DisableMemory;
            IsTracingOpLevelStorage = !options.DisableStorage;
        }

        bool IStateTracer.IsTracingState => false;
        bool IStorageTracer.IsTracingStorage => false;
        public bool IsTracingReceipt => true;
        public bool IsTracingActions => false;
        public bool IsTracingOpLevelStorage { get; }
        public bool IsTracingMemory { get; }
        public bool IsTracingInstructions => true;
        public bool IsTracingRefunds => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack { get; }
        public bool IsTracingBlockHash => false;
        public bool IsTracingAccess => false;
        public bool IsTracingFees => false;
        public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            _trace.ReturnValue = output;
            _trace.Gas = gasSpent;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Keccak? stateRoot = null)
        {
            _trace.Failed = true;
            _trace.ReturnValue = output ?? Array.Empty<byte>();
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge)
        {
            GethTxTraceEntry previousTraceEntry = _traceEntry;
            _traceEntry = new GethTxTraceEntry();
            _traceEntry.Pc = pc;
            _traceEntry.Operation = opcode.GetName(isPostMerge);
            _traceEntry.Gas = gas;
            _traceEntry.Depth = depth;
            _trace.Entries.Add(_traceEntry);

            if (_traceEntry.Depth > (previousTraceEntry?.Depth ?? 0))
            {
                _traceEntry.Storage = new Dictionary<string, string>();
                _trace.StoragesByDepth.Push(previousTraceEntry is not null ? previousTraceEntry.Storage : new Dictionary<string, string>());
            }
            else if (_traceEntry.Depth < (previousTraceEntry?.Depth ?? 0))
            {
                if (previousTraceEntry is null)
                {
                    throw new InvalidOperationException("Unexpected missing previous trace when leaving a call.");
                }

                _traceEntry.Storage = new Dictionary<string, string>(_trace.StoragesByDepth.Pop());
            }
            else
            {
                if (previousTraceEntry is null)
                {
                    throw new InvalidOperationException("Unexpected missing previous trace on continuation.");
                }

                _traceEntry.Storage = new Dictionary<string, string>(previousTraceEntry.Storage);
            }
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            _traceEntry.Error = GetErrorDescription(error);
        }

        private string? GetErrorDescription(EvmExceptionType evmExceptionType)
        {
            return evmExceptionType switch
            {
                EvmExceptionType.None => null,
                EvmExceptionType.BadInstruction => "BadInstruction",
                EvmExceptionType.StackOverflow => "StackOverflow",
                EvmExceptionType.StackUnderflow => "StackUnderflow",
                EvmExceptionType.OutOfGas => "OutOfGas",
                EvmExceptionType.InvalidSubroutineEntry => "InvalidSubroutineEntry",
                EvmExceptionType.InvalidSubroutineReturn => "InvalidSubroutineReturn",
                EvmExceptionType.InvalidJumpDestination => "BadJumpDestination",
                EvmExceptionType.AccessViolation => "AccessViolation",
                EvmExceptionType.StaticCallViolation => "StaticCallViolation",
                _ => "Error"
            };
        }

        public void ReportOperationRemainingGas(long gas)
        {
            _traceEntry.GasCost = _traceEntry.Gas - gas;
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            _traceEntry.UpdateMemorySize(newSize);
        }

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
            byte[] bigEndian = new byte[32];
            storageIndex.ToBigEndian(bigEndian);
            _traceEntry.Storage[bigEndian.ToHexString(false)] = new ZeroPaddedSpan(newValue, 32 - newValue.Length, PadDirection.Left).ToArray().ToHexString(false);
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
        }

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageRead(in StorageCell storageCell)
        {
            throw new NotSupportedException();
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

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            throw new NotSupportedException();
        }

        public void ReportBlockHash(Keccak blockHash)
        {
            throw new NotSupportedException();
        }

        public void ReportByteCode(byte[] byteCode)
        {
            throw new NotSupportedException();
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
        }

        public void ReportRefund(long refund)
        {
            throw new NotSupportedException();
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            throw new NotImplementedException();
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            throw new NotImplementedException();
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            _traceEntry.Stack = stackTrace;
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            _traceEntry.Memory = memoryTrace;
        }

        public void ReportFees(UInt256 fees, UInt256 burntFees)
        {
            throw new NotImplementedException();
        }

        public GethLikeTxTrace BuildResult()
        {
            return _trace;
        }
    }
}
