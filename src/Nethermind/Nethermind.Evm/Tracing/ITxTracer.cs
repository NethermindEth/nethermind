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

using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using Newtonsoft.Json.Serialization;

namespace Nethermind.Evm.Tracing
{
    public interface ITxTracer : IStateTracer, IStorageTracer
    {
        /// <summary>
        /// Defines whether MarkAsSuccess or MarkAsFailed will be called
        /// </summary>
        bool IsTracingReceipt => false;
        /// <summary>
        /// High level calls with information on the target account
        /// </summary>
        bool IsTracingActions => false;
        /// <summary>
        /// SSTORE and SLOAD level storage operations
        /// </summary>
        bool IsTracingOpLevelStorage => false;
        /// <summary>
        /// EVM memory access operations
        /// </summary>
        bool IsTracingMemory => false;
        bool IsTracingInstructions => false;
        /// <summary>
        /// Code deployment
        /// </summary>
        bool IsTracingCode => false;
        /// <summary>
        /// EVM stack tracing after each operation
        /// </summary>
        bool IsTracingStack => false;
        /// <summary>
        /// State changes at commit stage
        /// </summary>
        bool IsTracingState => false;

        /// <summary>
        /// Traces blockhash calls
        /// </summary>
        bool IsTracingBlockHash => false;

        void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null) => throw new NotSupportedException();

        void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null) => throw new NotSupportedException();

        void StartOperation(int depth, long gas, Instruction opcode, int pc) => throw new NotSupportedException();

        void ReportOperationError(EvmExceptionType error) => throw new NotSupportedException();

        void ReportOperationRemainingGas(long gas) => throw new NotSupportedException();

        void SetOperationStack(List<string> stackTrace) => throw new NotSupportedException();

        void ReportStackPush(Span<byte> stackItem) => throw new NotSupportedException();

        void SetOperationMemory(List<string> memoryTrace) => throw new NotSupportedException();

        void SetOperationMemorySize(ulong newSize) => throw new NotSupportedException();

        void ReportMemoryChange(long offset, Span<byte> data) => throw new NotSupportedException();

        void ReportStorageChange(Span<byte> key, Span<byte> value) => throw new NotSupportedException();

        void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue) => throw new NotSupportedException();

        void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) => throw new NotSupportedException();

        void ReportAction(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType, bool isPrecompileCall = false) => throw new NotSupportedException();

        void ReportActionEnd(long gas, byte[] output) => throw new NotSupportedException();

        void ReportActionError(EvmExceptionType evmExceptionType) => throw new NotSupportedException();

        void ReportActionEnd(long gas, Address deploymentAddress, byte[] deployedCode) => throw new NotSupportedException();

        void ReportBlockHash(Keccak blockHash) => throw new NotSupportedException();

        void ReportByteCode(byte[] byteCode) => throw new NotSupportedException();

        void ReportRefundForVmTrace(long refund, long gasAvailable) => throw new NotSupportedException();

        void ReportRefund(long refund) => throw new NotSupportedException();
    }
}