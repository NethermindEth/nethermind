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
    public class GethLikeTxTracer : TxTracer
    {
        private GethTxTraceEntry? _traceEntry;
        private readonly GethLikeTxTrace _trace = new();

        public GethLikeTxTracer(GethTraceOptions options)
        {
            IsTracingStack = !options.DisableStack;
            IsTracingMemory = !options.DisableMemory;
            IsTracingOpLevelStorage = !options.DisableStorage;
        }

        public sealed override bool IsTracingOpLevelStorage { get; protected set; }
        public override bool IsTracingReceipt => true;
        public sealed override bool IsTracingMemory { get; protected set; }
        public override bool IsTracingInstructions => true;
        public sealed override bool IsTracingStack { get; protected set; }

        public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            _trace.ReturnValue = output;
            _trace.Gas = gasSpent;
        }

        public override void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Keccak? stateRoot = null)
        {
            _trace.Failed = true;
            _trace.ReturnValue = output ?? Array.Empty<byte>();
        }

        public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
        {
            GethTxTraceEntry previousTraceEntry = _traceEntry;
            _traceEntry = new GethTxTraceEntry
            {
                Pc = pc,
                Operation = opcode.GetName(isPostMerge),
                Gas = gas,
                Depth = depth
            };
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

                _traceEntry.Storage = new Dictionary<string, string>(previousTraceEntry.Storage!);
            }
        }

        public override void ReportOperationError(EvmExceptionType error)
        {
            _traceEntry!.Error = GetErrorDescription(error);
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

        public override void ReportOperationRemainingGas(long gas)
        {
            _traceEntry!.GasCost = _traceEntry.Gas - gas;
        }

        public override void SetOperationMemorySize(ulong newSize)
        {
            _traceEntry!.UpdateMemorySize(newSize);
        }

        public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
            byte[] bigEndian = new byte[32];
            storageIndex.ToBigEndian(bigEndian);
            _traceEntry!.Storage![bigEndian.ToHexString(false)] = new ZeroPaddedSpan(newValue, 32 - newValue.Length, PadDirection.Left).ToArray().ToHexString(false);
        }

        public override void SetOperationStack(List<string> stackTrace)
        {
            _traceEntry!.Stack = stackTrace;
        }

        public override void SetOperationMemory(List<string> memoryTrace)
        {
            _traceEntry!.Memory = memoryTrace;
        }

        public GethLikeTxTrace BuildResult()
        {
            return _trace;
        }
    }
}
