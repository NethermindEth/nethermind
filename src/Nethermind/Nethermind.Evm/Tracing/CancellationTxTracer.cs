// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class CancellationTxTracer : ITxTracer, ITxTracerWrapper
    {
        private readonly ITxTracer _innerTracer;
        private readonly CancellationToken _token;
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
        public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees;

        public ITxTracer InnerTracer => _innerTracer;

        public CancellationTxTracer(ITxTracer innerTracer, CancellationToken token = default)
        {
            _innerTracer = innerTracer;
            _token = token;
        }

        public bool IsTracingReceipt
        {
            get => _isTracingReceipt || _innerTracer.IsTracingReceipt;
            init => _isTracingReceipt = value;
        }

        public bool IsTracingActions
        {
            get => _isTracingActions || _innerTracer.IsTracingActions;
            init => _isTracingActions = value;
        }

        public bool IsTracingOpLevelStorage
        {
            get => _isTracingOpLevelStorage || _innerTracer.IsTracingOpLevelStorage;
            init => _isTracingOpLevelStorage = value;
        }

        public bool IsTracingMemory
        {
            get => _isTracingMemory || _innerTracer.IsTracingMemory;
            init => _isTracingMemory = value;
        }

        public bool IsTracingInstructions
        {
            get => _isTracingInstructions || _innerTracer.IsTracingInstructions;
            init => _isTracingInstructions = value;
        }

        public bool IsTracingRefunds
        {
            get => _isTracingRefunds || _innerTracer.IsTracingRefunds;
            init => _isTracingRefunds = value;
        }

        public bool IsTracingCode
        {
            get => _isTracingCode || _innerTracer.IsTracingCode;
            init => _isTracingCode = value;
        }

        public bool IsTracingStack
        {
            get => _isTracingStack || _innerTracer.IsTracingStack;
            init => _isTracingStack = value;
        }

        public bool IsTracingState
        {
            get => _isTracingState || _innerTracer.IsTracingState;
            init => _isTracingState = value;
        }

        public bool IsTracingStorage
        {
            get => _isTracingStorage || _innerTracer.IsTracingStorage;
            init => _isTracingStorage = value;
        }

        public bool IsTracingBlockHash
        {
            get => _isTracingBlockHash || _innerTracer.IsTracingBlockHash;
            init => _isTracingBlockHash = value;
        }

        public bool IsTracingAccess
        {
            get => _isTracingBlockAccess || _innerTracer.IsTracingAccess;
            init => _isTracingBlockAccess = value;
        }

        public bool IsTracingFees
        {
            get => _isTracingFees || _innerTracer.IsTracingFees;
            init => _isTracingFees = value;
        }

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingState)
            {
                _innerTracer.ReportBalanceChange(address, before, after);
            }
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingState)
            {
                _innerTracer.ReportCodeChange(address, before, after);
            }
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingState)
            {
                _innerTracer.ReportNonceChange(address, before, after);
            }
        }

        public void ReportAccountRead(Address address)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingState)
            {
                _innerTracer.ReportAccountRead(address);
            }
        }

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingStorage)
            {
                _innerTracer.ReportStorageChange(storageCell, before, after);
            }
        }

        public void ReportStorageRead(in StorageCell storageCell)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingStorage)
            {
                _innerTracer.ReportStorageRead(storageCell);
            }
        }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingReceipt)
            {
                _innerTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
            }
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingReceipt)
            {
                _innerTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
            }
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.StartOperation(depth, gas, opcode, pc, isPostMerge);
            }
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportOperationError(error);
            }
        }

        public void ReportOperationRemainingGas(long gas)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportOperationRemainingGas(gas);
            }
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingStack)
            {
                _innerTracer.SetOperationStack(stackTrace);
            }
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportStackPush(stackItem);
            }
        }

        public void ReportStackPush(in ZeroPaddedSpan stackItem)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportStackPush(stackItem);
            }
        }

        public void ReportStackPush(byte stackItem)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportStackPush(stackItem);
            }
        }

        public void SetOperationMemory(IEnumerable<string> memoryTrace)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingMemory)
            {
                _innerTracer.SetOperationMemory(memoryTrace);
            }
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingMemory)
            {
                _innerTracer.SetOperationMemorySize(newSize);
            }
        }

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportMemoryChange(offset, data);
            }
        }

        public void ReportMemoryChange(long offset, in ZeroPaddedSpan data)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportMemoryChange(offset, data);
            }
        }

        public void ReportMemoryChange(long offset, byte data)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportMemoryChange(offset, data);
            }
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportStorageChange(key, value);
            }
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingOpLevelStorage)
            {
                _innerTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);
            }
        }

        public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingOpLevelStorage)
            {
                _innerTracer.LoadOperationStorage(address, storageIndex, value);
            }
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingActions)
            {
                _innerTracer.ReportSelfDestruct(address, balance, refundAddress);
            }
        }

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingActions)
            {
                _innerTracer.ReportAction(gas, value, @from, to, input, callType, isPrecompileCall);
            }
        }

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingActions)
            {
                _innerTracer.ReportActionEnd(gas, output);
            }
        }

        public void ReportActionError(EvmExceptionType evmExceptionType)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingActions)
            {
                _innerTracer.ReportActionError(evmExceptionType);
            }
        }

        public void ReportActionError(EvmExceptionType evmExceptionType, long gasLeft)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingActions)
            {
                _innerTracer.ReportActionError(evmExceptionType, gasLeft);
            }
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingActions)
            {
                _innerTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);
            }
        }

        public void ReportBlockHash(Keccak blockHash)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingBlockHash)
            {
                _innerTracer.ReportBlockHash(blockHash);
            }
        }

        public void ReportByteCode(byte[] byteCode)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingCode)
            {
                _innerTracer.ReportByteCode(byteCode);
            }
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);
            }
        }

        public void ReportRefund(long refund)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingRefunds)
            {
                _innerTracer.ReportRefund(refund);
            }
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingRefunds)
            {
                _innerTracer.ReportExtraGasPressure(extraGasPressure);
            }
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingAccess)
            {
                _innerTracer.ReportAccess(accessedAddresses, accessedStorageCells);
            }
        }

        public void ReportFees(UInt256 fees, UInt256 burntFees)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingFees)
            {
                _innerTracer.ReportFees(fees, burntFees);
            }
        }
    }
}
