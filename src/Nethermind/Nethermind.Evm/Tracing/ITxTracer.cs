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

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public interface ITxTracer : IStateTracer, IStorageTracer
    {
        /// <summary>
        /// Defines whether MarkAsSuccess or MarkAsFailed will be called
        /// </summary>
        bool IsTracingReceipt { get; }
        /// <summary>
        /// High level calls with information on the target account
        /// </summary>
        bool IsTracingActions { get; }
        /// <summary>
        /// SSTORE and SLOAD level storage operations
        /// </summary>
        bool IsTracingOpLevelStorage { get; }
        /// <summary>
        /// EVM memory access operations
        /// </summary>
        bool IsTracingMemory { get; }
        bool IsTracingInstructions { get; }
        /// <summary>
        /// Updates of refund counter
        /// </summary>
        bool IsTracingRefunds { get; }
        /// <summary>
        /// Code deployment
        /// </summary>
        bool IsTracingCode { get; }
        /// <summary>
        /// EVM stack tracing after each operation
        /// </summary>
        bool IsTracingStack { get; }

        /// <summary>
        /// Traces blockhash calls
        /// </summary>
        bool IsTracingBlockHash { get; }
        
        /// <summary>
        /// Traces storage access
        /// </summary>
        bool IsTracingAccess { get; }

        void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null);

        void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null);

        void StartOperation(int depth, long gas, Instruction opcode, int pc);

        void ReportOperationError(EvmExceptionType error);

        void ReportOperationRemainingGas(long gas);

        void SetOperationStack(List<string> stackTrace);

        void ReportStackPush(in ReadOnlySpan<byte> stackItem);
        
        void ReportStackPush(byte stackItem)
        {
            ReportStackPush(new[] {stackItem});
        }

        void ReportStackPush(in ZeroPaddedSpan stackItem)
        {
            ReportStackPush(stackItem.ToArray().AsSpan());
        }
        
        void ReportStackPush(in ZeroPaddedMemory stackItem)
        {
            ReportStackPush(stackItem.ToArray().AsSpan());
        }

        void SetOperationMemory(List<string> memoryTrace);

        void SetOperationMemorySize(ulong newSize);

        void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data);

        void ReportMemoryChange(long offset, byte data)
        {
            ReportMemoryChange(offset, new[] {data});
        }

        void ReportMemoryChange(long offset, in ZeroPaddedSpan data)
        {
            ReportMemoryChange(offset, data.ToArray());
        }
        
        void ReportMemoryChange(long offset, in ZeroPaddedMemory data)
        {
            ReportMemoryChange(offset, data.ToArray());
        }

        void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value);

        void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue);

        void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress);

        void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false);

        void ReportActionEnd(long gas, ReadOnlyMemory<byte> output);

        void ReportActionError(EvmExceptionType evmExceptionType);

        void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode);

        void ReportBlockHash(Keccak blockHash);

        void ReportByteCode(byte[] byteCode);

        /// <summary>
        /// Special case for VM trace in Parity but we consider removing support for it
        /// </summary>
        /// <param name="refund"></param>
        /// <param name="gasAvailable"></param>
        void ReportGasUpdateForVmTrace(long refund, long gasAvailable);

        void ReportRefund(long refund);
        void ReportExtraGasPressure(long extraGasPressure);
        void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells);
    }
}
