//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class CancellationTxTracer : ITxTracer
    {
        private readonly ITxTracer _innerTracer;
        private readonly CancellationToken _token;
        private bool _isTracingReceipt;
        private bool _isTracingActions;
        private bool _isTracingOpLevelStorage;
        private bool _isTracingMemory;
        private bool _isTracingInstructions;
        private bool _isTracingRefunds;
        private bool _isTracingCode;
        private bool _isTracingStack;
        private bool _isTracingState;
        private bool _isTracingBlockHash;

        public CancellationTxTracer(ITxTracer innerTracer, CancellationToken token = default)
        {
            _innerTracer = innerTracer;
            _token = token;
        }
        
        public bool IsTracingReceipt
        {
            get => _isTracingReceipt || _innerTracer.IsTracingReceipt;
            set => _isTracingReceipt = value;
        }

        public bool IsTracingActions
        {
            get => _isTracingActions || _innerTracer.IsTracingActions;
            set => _isTracingActions = value;
        }

        public bool IsTracingOpLevelStorage
        {
            get => _isTracingOpLevelStorage || _innerTracer.IsTracingOpLevelStorage;
            set => _isTracingOpLevelStorage = value;
        }

        public bool IsTracingMemory
        {
            get => _isTracingMemory || _innerTracer.IsTracingMemory;
            set => _isTracingMemory = value;
        }

        public bool IsTracingInstructions
        {
            get => _isTracingInstructions || _innerTracer.IsTracingInstructions;
            set => _isTracingInstructions = value;
        }

        public bool IsTracingRefunds
        {
            get => _isTracingRefunds || _innerTracer.IsTracingRefunds;
            set => _isTracingRefunds = value;
        }

        public bool IsTracingCode
        {
            get => _isTracingCode || _innerTracer.IsTracingCode;
            set => _isTracingCode = value;
        }

        public bool IsTracingStack
        {
            get => _isTracingStack || _innerTracer.IsTracingStack;
            set => _isTracingStack = value;
        }

        public bool IsTracingState
        {
            get => _isTracingState || _innerTracer.IsTracingState;
            set => _isTracingState = value;
        }

        public bool IsTracingBlockHash
        {
            get => _isTracingBlockHash || _innerTracer.IsTracingBlockHash;
            set => _isTracingBlockHash = value;
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

        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingState)
            {
                _innerTracer.ReportStorageChange(storageCell, before, after);
            }
        }
        
        public void ReportStorageRead(StorageCell storageCell)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingState)
            {
                _innerTracer.ReportStorageRead(storageCell);
            }
        }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingReceipt)
            {
                _innerTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
            }
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingReceipt)
            {
                _innerTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
            }
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.StartOperation(depth, gas, opcode, pc);
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

        public void ReportStackPush(in Span<byte> stackItem)
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

        public void SetOperationMemory(List<string> memoryTrace)
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

        public void ReportMemoryChange(long offset, in Span<byte> data)
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

        public void ReportStorageChange(in Span<byte> key, in Span<byte> value)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingInstructions)
            {
                _innerTracer.ReportStorageChange(key, value);
            }
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingOpLevelStorage)
            {
                _innerTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);
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

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType, bool isPrecompileCall = false)
        {
            _token.ThrowIfCancellationRequested();
            if (_innerTracer.IsTracingActions)
            {
                _innerTracer.ReportAction(gas, value, @from, to, input, callType, isPrecompileCall);
            }
        }

        public void ReportActionEnd(long gas, byte[] output)
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

        public void ReportActionEnd(long gas, Address deploymentAddress, byte[] deployedCode)
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
            _innerTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);
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
    }
}
