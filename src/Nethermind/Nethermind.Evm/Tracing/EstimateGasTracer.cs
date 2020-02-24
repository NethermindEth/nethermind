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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class EstimateGasTracer : ITxTracer
    {
        public bool IsTracingReceipt => true;
        public bool IsTracingActions => true;
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingState => false;
        public bool IsTracingBlockHash => false;

        public byte[] ReturnValue { get; set; }

        public long GasSpent { get; set; }

        public long ExcessiveGas
        {
            get
            {
                if (_gasOnEnd.Count == 0)
                {
                    return 0;
                }
                
                return _gasOnEnd.Min(g => g.Excess);
            }
        }

        public string Error { get; set; }

        public byte StatusCode { get; set; }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            GasSpent = gasSpent;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            GasSpent = gasSpent;
            Error = error;
            ReturnValue = output ?? Bytes.Empty;
            StatusCode = Evm.StatusCode.Failure;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            throw new NotSupportedException();
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            throw new NotSupportedException();
        }

        public void ReportOperationRemainingGas(long gas)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            throw new NotSupportedException();
        }

        public void ReportStackPush(Span<byte> stackItem)
        {
            throw new NotSupportedException();
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            throw new NotSupportedException();
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            throw new NotSupportedException();
        }

        public void ReportMemoryChange(long offset, Span<byte> data)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageChange(Span<byte> key, Span<byte> value)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue)
        {
            throw new NotSupportedException();
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
            throw new NotSupportedException();
        }

        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
        }

        private struct GasLeftAndNestingLevel
        {
            public GasLeftAndNestingLevel(long gasLeft, int nestingLevel)
            {
                GasLeft = gasLeft;
                NestingLevel = nestingLevel;
            }

            public long GasLeft { get; set; }
            public int NestingLevel { get; set; }

            public long Excess
            {
                get
                {
                    long excess = GasLeft;
                    for (int i = 0; i < NestingLevel; i++)
                    {
                        excess = (long) Math.Floor(excess * 64m / 63);    
                    }

                    return excess;
                           
                }
            }
        }

        private List<GasLeftAndNestingLevel> _gasOnEnd = new List<GasLeftAndNestingLevel>();

        private int _currentNestingLevel = -1;

        private bool _isInPrecompile = false;

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType, bool isPrecompileCall = false)
        {
            if (!isPrecompileCall)
            {
                _currentNestingLevel++;
            }
            else
            {
                _isInPrecompile = true;
            }
        }

        public void ReportActionEnd(long gas, byte[] output)
        {
            if (!_isInPrecompile)
            {
                _gasOnEnd.Add(new GasLeftAndNestingLevel(gas, _currentNestingLevel--));
            }
            else
            {
                _isInPrecompile = false;
            }
        }

        public void ReportActionError(EvmExceptionType exceptionType)
        {
            _currentNestingLevel--;
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, byte[] deployedCode)
        {
            if (!_isInPrecompile)
            {
                _gasOnEnd.Add(new GasLeftAndNestingLevel(gas, _currentNestingLevel--));
            }
            else
            {
                _isInPrecompile = false;
            }
        }

        public void ReportBlockHash(Keccak blockHash)
        {
            throw new NotSupportedException();
        }

        public void ReportByteCode(byte[] byteCode)
        {
            throw new NotSupportedException();
        }

        public void ReportRefundForVmTrace(long refund, long gasAvailable)
        {
            throw new NotSupportedException();
        }

        public void ReportRefund(long refund)
        {
            throw new NotSupportedException();
        }
    }
}