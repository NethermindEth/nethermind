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
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.DataModel;
using Block = Nethermind.JsonRpc.DataModel.Block;
using StorageTrace = Nethermind.JsonRpc.DataModel.StorageTrace;
using Transaction = Nethermind.JsonRpc.DataModel.Transaction;
using TransactionReceipt = Nethermind.JsonRpc.DataModel.TransactionReceipt;
using TransactionTrace = Nethermind.JsonRpc.DataModel.TransactionTrace;
using TransactionTraceEntry = Nethermind.JsonRpc.DataModel.TransactionTraceEntry;
using StorageTraceEntry = Nethermind.JsonRpc.DataModel.StorageTraceEntry;

namespace Nethermind.JsonRpc
{
    public class JsonRpcModelMapper : IJsonRpcModelMapper
    {
        public Block MapBlock(Core.Block block, bool returnFullTransactionObjects)
        {
            Transaction[] transactions = null;
            Data[] transactionHashes = null;
            if (returnFullTransactionObjects)
            {
                transactions = new Transaction[block.Transactions.Length];
            }
            else
            {
                transactionHashes = new Data[block.Transactions.Length];
            }

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                if (returnFullTransactionObjects)
                {
                    transactions[i] = MapTransaction(block.Hash, block.Number, i, block.Transactions[i]);
                }
                else
                {
                    transactionHashes[i] = new Data(block.Transactions[i].Hash);
                }
            }
            
            var blockModel = new Block
            {
                Hash = new Data(block.Hash.Bytes),               
                Uncles = block.Ommers?.Select(x => new Data(x.Hash)).ToArray(),
                Transactions = transactions,
                TransactionHashes = transactionHashes
            };

            if (block.Header == null)
            {
                return blockModel;
            }

            blockModel.Number = new Quantity(block.Header.Number);
            blockModel.ParentHash = new Data(block.Header.ParentHash);
            blockModel.Nonce = new Data(block.Header.Nonce.ToString());
            blockModel.Sha3Uncles = new Data(block.Header.OmmersHash);
            blockModel.LogsBloom = new Data(block.Header.Bloom?.Bytes);
            blockModel.TransactionsRoot = new Data(block.Header.TransactionsRoot);
            blockModel.StateRoot = new Data(block.Header.StateRoot);
            blockModel.ReceiptsRoot = new Data(block.Header.ReceiptsRoot);
            blockModel.Miner = block.Header.Beneficiary != null ? new Data(block.Header.Beneficiary) : null;
            blockModel.Difficulty = new Quantity(block.Header.Difficulty);
            blockModel.TotalDifficulty = new Quantity(block.Header.TotalDifficulty ?? 0);
            blockModel.ExtraData = new Data(block.Header.ExtraData);
            //Size = new Quantity(block.Header.)
            blockModel.GasLimit = new Quantity(block.Header.GasLimit);
            blockModel.GasUsed = new Quantity(block.Header.GasUsed);
            blockModel.Timestamp = new Quantity(block.Header.Timestamp);

            return blockModel;
        }

        public Transaction MapTransaction(Keccak blockHash, UInt256 blockNumber, int index, Core.Transaction transaction)
        {   
            return new Transaction
            {
                Hash = new Data(transaction.Hash),
                Nonce = new Quantity(transaction.Nonce),
                BlockHash = new Data(blockHash),
                BlockNumber = new Quantity(blockNumber),
                TransactionIndex = new Quantity(index),
                From = new Data(transaction.SenderAddress),
                To = new Data(transaction.To),
                Value = new Quantity(transaction.Value),
                GasPrice = new Quantity(transaction.GasPrice),
                Gas = new Quantity(transaction.GasLimit),
                Data = new Data(transaction.Data)
            };
        }
        
        public Transaction MapTransaction(Core.TransactionReceipt receipt, Core.Transaction transaction)
        {   
            return new Transaction
            {
                Hash = new Data(transaction.Hash),
                Nonce = new Quantity(transaction.Nonce),
                BlockHash = new Data(receipt.BlockHash),
                BlockNumber = new Quantity(receipt.BlockNumber),
                TransactionIndex = new Quantity(receipt.Index),
                From = new Data(receipt.Sender),
                To = new Data(receipt.Recipient),
                Value = new Quantity(transaction.Value),
                GasPrice = new Quantity(transaction.GasPrice),
                Gas = new Quantity(transaction.GasLimit),
                Data = new Data(transaction.Data)
            };
        }

        public Core.Transaction MapTransaction(Transaction transaction)
        {
            Core.Transaction tx = new Core.Transaction();
            tx.GasLimit = transaction.Gas?.Value.ToUInt256() ?? 90000;
            tx.GasPrice = transaction.GasPrice?.Value.ToUInt256() ?? 0;
            tx.Nonce = transaction.Nonce?.Value.ToUInt256() ?? 0; // here pick the last nonce?
            tx.To = transaction.To == null ? null : new Address(transaction.To.Value);
            tx.SenderAddress = transaction.From == null ? null : new Address(transaction.From.Value);
            tx.Value = transaction.Value?.Value.ToUInt256() ?? UInt256.Zero;
            if (tx.To == null)
            {
                tx.Init = transaction.Data.Value;
            }
            else
            {
                tx.Data = transaction.Data.Value;
            }

            return tx;
        }

        public TransactionReceipt MapTransactionReceipt(Keccak hash, Core.TransactionReceipt receipt)
        {
            TransactionReceipt mapped = new TransactionReceipt
            {
                TransactionHash = new Data(hash),
                TransactionIndex = new Quantity(receipt.Index),
                BlockHash = new Data(receipt.BlockHash),
                BlockNumber = new Quantity(receipt.BlockNumber),
                CumulativeGasUsed = new Quantity(receipt.GasUsedTotal),
                GasUsed = new Quantity(receipt.GasUsed),
                From = new Data(receipt.Sender),
                To = new Data(receipt.Recipient),
                ContractAddress = new Data(receipt.ContractAddress),
                Logs = receipt.Logs?.Select(MapLog).ToArray() ?? Array.Empty<Log>(),
                LogsBloom = new Data(receipt.Bloom?.Bytes),
                Status = new Quantity(receipt.StatusCode)
            };

            for (int i = 0; i < mapped.Logs.Length; i++)
            {
                mapped.Logs[i].BlockHash = mapped.BlockHash;
                mapped.Logs[i].BlockNumber = mapped.BlockNumber;
                mapped.Logs[i].LogIndex = new Quantity(i);
                mapped.Logs[i].TransactionHash = mapped.TransactionHash;
                mapped.Logs[i].TransactionIndex = mapped.TransactionIndex;
            }

            return mapped;
        }

        public TransactionTrace MapTransactionTrace(Evm.Tracing.TransactionTrace transactionTrace)
        {
            if (transactionTrace == null)
            {
                return null;
            }
            
            return new TransactionTrace
            {
                Gas = transactionTrace.Gas,
                Failed = transactionTrace.Failed,
                ReturnValue = transactionTrace.ReturnValue,
                StorageTrace = MapStorageTrace(transactionTrace.StorageTrace),
                StructLogs = transactionTrace.Entries?.Select(x => new TransactionTraceEntry
                {
                    Pc = x.Pc,
                    Op = x.Operation,
                    Gas = x.Gas,
                    GasCost = x.GasCost,
                    Error = x.Error,
                    Depth = x.Depth,
                    Stack = x.Stack,
                    Memory = x.Memory,
                    Storage = x.Storage
                }).ToArray()
            };
        }
        
        public StorageTrace MapStorageTrace(Evm.Tracing.StorageTrace storageTrace)
        {
            if (storageTrace == null)
            {
                return null;
            }
            
            return new StorageTrace
            {
                Entries = storageTrace.Entries?.Select(x => new StorageTraceEntry
                {
                    Cost = x.Cost,
                    Refund = x.Refund,
                    OldValue= x.OldValue,
                    NewValue = x.NewValue,
                    Address = x.Address,
                    Key = ((BigInteger)x.Index).ToByteArray().ToHexString(),
                }).ToArray()
            };
        }

        public BlockTraceItem[] MapBlockTrace(BlockTrace blockTrace)
        {
            return blockTrace.TxTraces.Select(t => new BlockTraceItem(MapTransactionTrace(t))).ToArray();
        }

        public Log MapLog(LogEntry logEntry)
        {
            Log log = new Log();
            log.Data = new Data(logEntry.Data);
            log.Address = new Data(logEntry.LoggersAddress);
            log.Topics = logEntry.Topics.Select(t => new Data(t)).ToArray();
            return log;
        }
    }
}