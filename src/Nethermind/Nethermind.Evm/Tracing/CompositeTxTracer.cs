//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class CompositeTxTracer : ITxTracer
    {
        private readonly ITxTracer[] _txTracers;

        public CompositeTxTracer(params ITxTracer[] txTracers)
        {
            _txTracers = txTracers;
            for (var index = 0; index < txTracers.Length; index++)
            {
                var t = txTracers[index];
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

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingState)
                {
                    innerTracer.ReportBalanceChange(address, before, after);
                }
            }
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingState)
                {
                    innerTracer.ReportCodeChange(address, before, after);
                }
            }
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingState)
                {
                    innerTracer.ReportNonceChange(address, before, after);
                }
            }
        }

        public void ReportAccountRead(Address address)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingState)
                {
                    innerTracer.ReportAccountRead(address);
                }
            }
        }

        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingStorage)
                {
                    innerTracer.ReportStorageChange(storageCell, before, after);
                }
            }
        }
        
        public void ReportStorageRead(StorageCell storageCell)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingStorage)
                {
                    innerTracer.ReportStorageRead(storageCell);
                }
            }
        }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingReceipt)
                {
                    innerTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
                }
            }
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingReceipt)
                {
                    innerTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
                }
            }
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.StartOperation(depth, gas, opcode, pc);
                }
            }
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportOperationError(error);
                }
            }
        }

        public void ReportOperationRemainingGas(long gas)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportOperationRemainingGas(gas);
                }
            }
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingStack)
                {
                    innerTracer.SetOperationStack(stackTrace);
                }
            }
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportStackPush(stackItem);
                }
            }
        }

        public void ReportStackPush(in ZeroPaddedSpan stackItem)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportStackPush(stackItem);
                }
            }
        }

        public void ReportStackPush(byte stackItem)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportStackPush(stackItem);
                }
            }
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingMemory)
                {
                    innerTracer.SetOperationMemory(memoryTrace);
                }
            }
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingMemory)
                {
                    innerTracer.SetOperationMemorySize(newSize);
                }
            }
        }

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportMemoryChange(offset, data);
                }
            }
        }

        public void ReportMemoryChange(long offset, in ZeroPaddedSpan data)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportMemoryChange(offset, data);
                }
            }
        }

        public void ReportMemoryChange(long offset, byte data)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportMemoryChange(offset, data);
                }
            }
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportStorageChange(key, value);
                }
            }
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingOpLevelStorage)
                {
                    innerTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);
                }
            }
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingActions)
                {
                    innerTracer.ReportSelfDestruct(address, balance, refundAddress);
                }
            }
        }

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType, bool isPrecompileCall = false)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingActions)
                {
                    innerTracer.ReportAction(gas, value, @from, to, input, callType, isPrecompileCall);
                }
            }
        }

        public void ReportActionEnd(long gas, byte[] output)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingActions)
                {
                    innerTracer.ReportActionEnd(gas, output);
                }
            }
        }

        public void ReportActionError(EvmExceptionType evmExceptionType)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingActions)
                {
                    innerTracer.ReportActionError(evmExceptionType);
                }
            }
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, byte[] deployedCode)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingActions)
                {
                    innerTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);
                }
            }
        }

        public void ReportBlockHash(Keccak blockHash)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingBlockHash)
                {
                    innerTracer.ReportBlockHash(blockHash);
                }
            }
        }

        public void ReportByteCode(byte[] byteCode)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingCode)
                {
                    innerTracer.ReportByteCode(byteCode);
                }
            }
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingInstructions)
                {
                    innerTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);
                }
            }
        }

        public void ReportRefund(long refund)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingRefunds)
                {
                    innerTracer.ReportRefund(refund);
                }
            }
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingRefunds)
                {
                    innerTracer.ReportExtraGasPressure(extraGasPressure);
                }
            }
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            for (var index = 0; index < _txTracers.Length; index++)
            {
                var innerTracer = _txTracers[index];
                if (innerTracer.IsTracingAccess)
                {
                    innerTracer.ReportAccess(accessedAddresses, accessedStorageCells);
                }
            }
        }
    }
}
