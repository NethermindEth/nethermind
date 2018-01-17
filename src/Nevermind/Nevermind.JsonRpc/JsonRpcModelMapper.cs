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
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.JsonRpc.DataModel;
using Block = Nevermind.JsonRpc.DataModel.Block;
using Transaction = Nevermind.JsonRpc.DataModel.Transaction;
using TransactionReceipt = Nevermind.JsonRpc.DataModel.TransactionReceipt;

namespace Nevermind.JsonRpc
{
    public class JsonRpcModelMapper : IJsonRpcModelMapper
    {
        private readonly ISigner _signer;

        public JsonRpcModelMapper(ISigner signer)
        {
            _signer = signer;
        }

        public Block MapBlock(Core.Block block, bool returnFullTransactionObjects)
        {
            var blockModel = new Block
            {
                Hash = new Data(block.Hash.Bytes),               
                Uncles = block.Ommers?.Select(x => new Data(x.Hash.Bytes)).ToArray(),
                Transactions = returnFullTransactionObjects ? block.Transactions?.Select(x => MapTransaction(x, block)).ToArray() : null,
                TransactionHashes = !returnFullTransactionObjects ? block.Transactions?.Select(x => new Data(x.Hash.Bytes)).ToArray() : null
            };

            if (block.Header == null)
            {
                return blockModel;
            }

            blockModel.Number = new Quantity(block.Header.Number);
            blockModel.ParentHash = new Data(block.Header.ParentHash.Bytes);
            blockModel.Nonce = new Data(block.Header.Nonce.ToString());
            blockModel.Sha3Uncles = new Data(block.Header.OmmersHash.Bytes);
            blockModel.LogsBloom = new Data(block.Header.Bloom?.Bytes);
            blockModel.TransactionsRoot = new Data(block.Header.TransactionsRoot.Bytes);
            blockModel.StateRoot = new Data(block.Header.StateRoot.Bytes);
            blockModel.ReceiptsRoot = new Data(block.Header.ReceiptsRoot.Bytes);
            blockModel.Miner = block.Header.Beneficiary != null ? new Data(block.Header.Beneficiary.Hex) : null;
            blockModel.Difficulty = new Quantity(block.Header.Difficulty);
            //TotalDifficulty = new Quantity(block.Header.Difficulty),
            blockModel.ExtraData = new Data(block.Header.ExtraData);
            //Size = new Quantity(block.Header.)
            blockModel.GasLimit = new Quantity(block.Header.GasLimit);
            blockModel.GasUsed = new Quantity(block.Header.GasUsed);
            blockModel.Timestamp = new Quantity(block.Header.Timestamp);

            return blockModel;
        }

        public Transaction MapTransaction(Core.Transaction transaction, Core.Block block)
        {
            return new Transaction
            {
                Hash = new Data(transaction.Hash.Bytes),
                Nonce = new Quantity(transaction.Nonce),
                BlockHash = block != null ? new Data(block.Hash.Bytes) : null,
                BlockNumber = block?.Header != null ? new Quantity(block.Header.Number) : null,
                TransactionIndex = block?.Transactions != null ? new Quantity(GetTransactionIndex(transaction, block)) : null,
                From = new Data(_signer.Recover(transaction).Hex),
                To = new Data(transaction.To.Hex),
                Value = new Quantity(transaction.Value),
                GasPrice = new Quantity(transaction.GasPrice),
                Gas = new Quantity(transaction.GasLimit),
                Data = new Data(transaction.Data)
            };
        }

        public TransactionReceipt MapTransactionReceipt(Core.TransactionReceipt receipt, Core.Transaction transaction, Core.Block block)
        {
            return new TransactionReceipt
            {
                TransactionHash = new Data(transaction.Hash.Bytes),
                TransactionIndex = block?.Transactions != null ? new Quantity(GetTransactionIndex(transaction, block)) : null,
                BlockHash = block != null ? new Data(block.Hash.Bytes) : null,
                BlockNumber = block?.Header != null ? new Quantity(block.Header.Number) : null,
                //CumulativeGasUsed = new Quantity(receipt.GasUsed),
                GasUsed = new Quantity(receipt.GasUsed),
                ContractAddress = transaction.IsContractCreation && receipt.Recipient != null ? new Data(receipt.Recipient.Hex) : null,
                Logs = receipt.Logs?.Select(MapLog).ToArray(),
                LogsBloom = new Data(receipt.Bloom?.Bytes),
                Status = new Quantity(receipt.StatusCode)
            };
        }

        public Log MapLog(LogEntry logEntry)
        {
            throw new System.NotImplementedException();
        }

        private BigInteger GetTransactionIndex(Core.Transaction transaction, Core.Block block)
        {
            for (var i = 0; i < block.Transactions.Count; i++)
            {
                var trans = block.Transactions[i];
                if (trans.Hash.Equals(transaction.Hash))
                {
                    return i;
                }
            }
            throw new Exception($"Cannot find transaction in block transactions based on transaction hash: {transaction.Hash}, blockHash: {block.Hash}.");
        }
    }
}