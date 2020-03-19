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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class BlockReceiptsTracer : IBlockTracer,  ITxTracer
    {
        private Block _block;
        public bool IsTracingReceipt => true;
        public bool IsTracingActions => _currentTxTracer.IsTracingActions;
        public bool IsTracingOpLevelStorage => _currentTxTracer.IsTracingOpLevelStorage;
        public bool IsTracingMemory => _currentTxTracer.IsTracingMemory;
        public bool IsTracingInstructions => _currentTxTracer.IsTracingInstructions;
        public bool IsTracingRefunds => _currentTxTracer.IsTracingRefunds;
        public bool IsTracingCode => _currentTxTracer.IsTracingCode;
        public bool IsTracingStack => _currentTxTracer.IsTracingStack;
        public bool IsTracingState => _currentTxTracer.IsTracingState;
        
        public bool IsTracingBlockHash => _currentTxTracer.IsTracingBlockHash;

        private IBlockTracer _otherTracer;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            TxReceipts[_currentIndex] = BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot);
            
            // hacky way to support nested receipt tracers
            if (_otherTracer is ITxTracer otherTxTracer)
            {
                otherTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
            }
            
            if (_currentTxTracer.IsTracingReceipt)
            {
                _currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs);
            }
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            TxReceipts[_currentIndex] = BuildFailedReceipt(recipient, gasSpent, error, stateRoot);
            
            // hacky way to support nested receipt tracers
            if (_otherTracer is ITxTracer otherTxTracer)
            {
                otherTxTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
            }
            
            if (_currentTxTracer.IsTracingReceipt)
            {
                _currentTxTracer.MarkAsFailed(recipient, gasSpent, output, error);
            }
        }

        private TxReceipt BuildFailedReceipt(Address recipient, long gasSpent, string error, Keccak stateRoot = null)
        {
            TxReceipt receipt = BuildReceipt(recipient, gasSpent, StatusCode.Failure, LogEntry.EmptyLogs, stateRoot);
            receipt.Error = error;
            return receipt;
        }

        private TxReceipt BuildReceipt(Address recipient, long spentGas, byte statusCode, LogEntry[] logEntries, Keccak stateRoot = null)
        {
            Transaction transaction = _block.Transactions[_currentIndex];
            TxReceipt txReceipt = new TxReceipt();
            txReceipt.Logs = logEntries;
            if (logEntries.Length > 0)
            {
                if (_block.Bloom == Bloom.Empty)
                {
                    _block.Header.Bloom = new Bloom();
                }
            }
            
            txReceipt.Bloom = logEntries.Length == 0 ? Bloom.Empty : new Bloom(logEntries, _block.Bloom);
            txReceipt.GasUsedTotal = _block.GasUsed;
            txReceipt.StatusCode = statusCode;
            txReceipt.Recipient = transaction.IsContractCreation ? null : recipient;
            txReceipt.BlockHash = _block.Hash;
            txReceipt.BlockNumber = _block.Number;
            txReceipt.Index = _currentIndex;
            txReceipt.GasUsed = spentGas;
            txReceipt.Sender = transaction.SenderAddress;
            txReceipt.ContractAddress = transaction.IsContractCreation ? recipient : null;
            txReceipt.TxHash = transaction.Hash;
            txReceipt.PostTransactionState = stateRoot;

            return txReceipt;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            _currentTxTracer.StartOperation(depth, gas, opcode, pc);
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            _currentTxTracer.ReportOperationError(error);
        }

        public void ReportOperationRemainingGas(long gas)
        {
            _currentTxTracer.ReportOperationRemainingGas(gas);
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            _currentTxTracer.SetOperationMemorySize(newSize);
        }

        public void ReportMemoryChange(long offset, Span<byte> data)
        {
            _currentTxTracer.ReportMemoryChange(offset, data);
        }

        public void ReportStorageChange(Span<byte> key, Span<byte> value)
        {
            _currentTxTracer.ReportStorageChange(key, value);
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue)
        {
            _currentTxTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            _currentTxTracer.ReportSelfDestruct(address, balance, refundAddress);
        }

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            _currentTxTracer.ReportBalanceChange(address, before, after);
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            _currentTxTracer.ReportCodeChange(address, before, after);
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            _currentTxTracer.ReportNonceChange(address, before, after);
        }
        
        public void ReportAccountRead(Address address)
        {
            _currentTxTracer.ReportAccountRead(address);
        }

        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after)
        {
            _currentTxTracer.ReportStorageChange(storageCell, before, after);
        }
        
        public void ReportStorageRead(StorageCell storageCell)
        {
            _currentTxTracer.ReportStorageRead(storageCell);
        }

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType, bool isPrecompileCall = false)
        {
            _currentTxTracer.ReportAction(gas, value, @from, to, input, callType, isPrecompileCall);
        }

        public void ReportActionEnd(long gas, byte[] output)
        {
            _currentTxTracer.ReportActionEnd(gas, output);
        }

        public void ReportActionError(EvmExceptionType exceptionType)
        {
            _currentTxTracer.ReportActionError(exceptionType);
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, byte[] deployedCode)
        {
            _currentTxTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);
        }

        public void ReportByteCode(byte[] byteCode)
        {
            _currentTxTracer.ReportByteCode(byteCode);
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
            _currentTxTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);
        }

        public void ReportRefund(long refund)
        {
            _currentTxTracer.ReportRefund(refund);
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            _currentTxTracer.ReportExtraGasPressure(extraGasPressure);
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            _currentTxTracer.SetOperationStack(stackTrace);
        }

        public void ReportStackPush(Span<byte> stackItem)
        {
            _currentTxTracer.ReportStackPush(stackItem);
        }
        
        public void ReportBlockHash(Keccak blockHash)
        {
            _currentTxTracer.ReportBlockHash(blockHash);
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            _currentTxTracer.SetOperationMemory(memoryTrace);
        }

        private ITxTracer _currentTxTracer;
        private int _currentIndex;
        public TxReceipt[] TxReceipts { get; private set; }

        public bool IsTracingRewards => _otherTracer.IsTracingRewards;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            _otherTracer.ReportReward(author, rewardType, rewardValue);
        }

        public void BeforeRestore(IStateProvider state)
        {
            _otherTracer.BeforeRestore(state);
        }

        public void StartNewBlockTrace(Block block)
        {
            if (_otherTracer == null)
            {
                throw new InvalidOperationException("other tracer not set in receipts tracer");
            }
            
            _block = block;
            _currentIndex = 0;
            TxReceipts = new TxReceipt[_block.Transactions.Length];
            _otherTracer.StartNewBlockTrace(block);
        }

        public ITxTracer StartNewTxTrace(Keccak txHash)
        {
            _currentTxTracer = _otherTracer.StartNewTxTrace(txHash);
            return _currentTxTracer;
        }

        public void EndTxTrace()
        {
            _otherTracer.EndTxTrace();
            _currentIndex++;
        }

        public void SetOtherTracer(IBlockTracer blockTracer)
        {
            _otherTracer = blockTracer;
        }
    }
}