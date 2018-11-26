/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Evm.Tracing
{
    public class BlockReceiptsTracer : IBlockTracer, ITxTracer
    {
        private readonly Block _block;
        private readonly ISpecProvider _specProvider;
        private readonly IStateProvider _stateProvider;
        public bool IsTracingReceipt => true;
        public bool IsTracingCalls => _currentTxTracer.IsTracingCalls;
        public bool IsTracingOpLevelStorage => _currentTxTracer.IsTracingOpLevelStorage;
        public bool IsTracingMemory => _currentTxTracer.IsTracingMemory;
        public bool IsTracingInstructions => _currentTxTracer.IsTracingInstructions;
        public bool IsTracingStack => _currentTxTracer.IsTracingStack;
        public bool IsTracingState => _currentTxTracer.IsTracingState;

        private IBlockTracer _otherTracer;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs)
        {
            Receipts[_currentIndex] = BuildReceipt(recipient, gasSpent, StatusCode.Success, logs);
            if (_currentTxTracer.IsTracingReceipt)
            {
                _currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs);
            }
        }

        public void MarkAsFailed(Address recipient, long gasSpent)
        {
            Receipts[_currentIndex] = BuildFailedReceipt(recipient, gasSpent);
            if (_currentTxTracer.IsTracingReceipt)
            {
                _currentTxTracer.MarkAsFailed(recipient, gasSpent);
            }
        }

        private TransactionReceipt BuildFailedReceipt(Address recipient, long gasSpent)
        {
            return BuildReceipt(recipient, gasSpent, StatusCode.Failure, LogEntry.EmptyLogs);
        }

        private TransactionReceipt BuildReceipt(Address recipient, long spentGas, byte statusCode, LogEntry[] logEntries)
        {
            Transaction transaction = _block.Transactions[_currentIndex];
            TransactionReceipt transactionReceipt = new TransactionReceipt();
            transactionReceipt.Logs = logEntries;
            transactionReceipt.Bloom = logEntries.Length == 0 ? Bloom.Empty : new Bloom(logEntries);
            transactionReceipt.GasUsedTotal = _block.GasUsed;
            if (!_specProvider.GetSpec(_block.Number).IsEip658Enabled)
            {
                transactionReceipt.PostTransactionState = _stateProvider.StateRoot;
            }

            transactionReceipt.StatusCode = statusCode;
            transactionReceipt.Recipient = transaction.IsContractCreation ? null : recipient;

            transactionReceipt.BlockHash = _block.Hash;
            transactionReceipt.BlockNumber = _block.Number;
            transactionReceipt.Index = _currentIndex;
            transactionReceipt.GasUsed = spentGas;
            transactionReceipt.Sender = transaction.SenderAddress;
            transactionReceipt.ContractAddress = transaction.IsContractCreation ? recipient : null;
            transactionReceipt.TransactionHash = transaction.Hash;

            return transactionReceipt;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            _currentTxTracer.StartOperation(depth, gas, opcode, pc);
        }

        public void SetOperationError(string error)
        {
            _currentTxTracer.SetOperationError(error);
        }

        public void SetOperationRemainingGas(long gas)
        {
            _currentTxTracer.SetOperationRemainingGas(gas);
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            _currentTxTracer.SetOperationMemorySize(newSize);
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue, long cost, long refund)
        {
            _currentTxTracer.SetOperationStorage(address, storageIndex, newValue, currentValue, cost, refund);
        }

        public void ReportBalanceChange(Address address, UInt256 before, UInt256 after)
        {
            _currentTxTracer.ReportBalanceChange(address, before, after);
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            _currentTxTracer.ReportCodeChange(address, before, after);
        }

        public void ReportNonceChange(Address address, UInt256 before, UInt256 after)
        {
            _currentTxTracer.ReportNonceChange(address, before, after);
        }

        public void ReportStorageChange(StorageAddress storageAddress, byte[] before, byte[] after)
        {
            _currentTxTracer.ReportStorageChange(storageAddress, before, after);
        }

        public void ReportCall(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType)
        {
            _currentTxTracer.ReportCall(gas, value, @from, to, input, callType);
        }

        public void ReportCallEnd(long gas, byte[] output)
        {
            _currentTxTracer.ReportCallEnd(gas, output);
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            _currentTxTracer.SetOperationStack(stackTrace);
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            _currentTxTracer.SetOperationMemory(memoryTrace);
        }

        private ITxTracer _currentTxTracer;
        private int _currentIndex;
        public TransactionReceipt[] Receipts { get; }

        public bool IsTracingRewards => _otherTracer.IsTracingRewards;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            _otherTracer.ReportReward(author, rewardType, rewardValue);
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

        public BlockReceiptsTracer(Block block, IBlockTracer otherTracer, ISpecProvider specProvider, IStateProvider stateProvider)
        {
            _block = block;
            _otherTracer = otherTracer ?? throw new ArgumentNullException(nameof(otherTracer));
            Receipts = new TransactionReceipt[_block.Transactions.Length];
            
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        }
    }
}