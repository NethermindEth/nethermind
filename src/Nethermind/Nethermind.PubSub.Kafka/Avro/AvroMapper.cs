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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.PubSub.Kafka.Avro.Models;

namespace Nethermind.PubSub.Kafka.Avro
{
    public class AvroMapper : IAvroMapper
    {
        private readonly IBlockTree _blockTree;

        public AvroMapper(IBlockTree blockTree)
        {
            _blockTree = blockTree;
        }

        public Block MapBlock(Core.Block block)
            => new Block
            {
                difficulty = block.Difficulty.ToString(),
                blockHash = block.Hash.ToString(),
                parentHash = block.ParentHash.ToString(),
                gasLimit = block.GasLimit,
                gasUsed = block.GasUsed,
                blockNumber = (long) block.Number,
                timestamp = (long) block.Timestamp,
                extraData = block.Header.ExtraData,
                miner = block.Beneficiary.ToString(),
                nonce = block.Header.Nonce.ToString(),
                size = Rlp.Encode(block).Bytes.Length,
                transactions = block.Transactions?
                                   .Select((t, i) => MapTransaction(i, block.Number, block.Hash, t)).ToList() ??
                               new List<Transaction>(),
                logsBloom = block.Header.Bloom.ToString(),
                receiptsRoot = block.Header.ReceiptsRoot.ToString(),
                stateRoot = block.Header.StateRoot.ToString(),
                transactionRoot = block.TransactionsRoot.ToString(),
                totalDifficulty = block.TotalDifficulty?.ToString() ?? string.Empty,
                uncles = block.Ommers?.Select(o => o.Hash.ToString()).ToList() ?? new List<string>(),
                sha3uncles = block.Header.OmmersHash.ToString()
            };

        public Transaction MapTransaction(int index, long blockNumber,
            Keccak blockHash, Core.Transaction transaction)
            => new Transaction
            {
                transactionIndex = index,
                blockNumber = blockNumber,
                toAddr = transaction.To?.ToString() ?? string.Empty,
                blockHash = blockHash.ToString(),
                nonce = (int) transaction.Nonce,
                fromAddr = transaction.SenderAddress?.ToString() ?? string.Empty,
                hash = transaction.Hash.ToString(),
                gasPrice = (long) transaction.GasPrice,
                v = transaction.Signature.V,
                r = transaction.Signature.R.ToString(),
                s = transaction.Signature.S.ToString(),
                input = transaction.Data ?? new byte[0],
                gas = (long) transaction.GasLimit,
                weiValue = transaction.Value.ToString()
            };

        public FullTransaction MapFullTransaction(Core.FullTransaction fullTransaction)
        {
            var index = fullTransaction.Index;
            var transaction = fullTransaction.Transaction;
            var receipt = fullTransaction.Receipt;
            var removed = !_blockTree.IsMainChain(receipt.BlockHash);

            return new FullTransaction
            {
                minedAt = (long) _blockTree.FindBlock(receipt.BlockHash, BlockTreeLookupOptions.None).Timestamp,
                blockNumber = (long) receipt.BlockNumber,
                receipt = new Receipt
                {
                    transactionIndex = index,
                    blockNumber = (long) receipt.BlockNumber,
                    toAddr = receipt.Recipient?.ToString() ?? string.Empty,
                    blockHash = receipt.BlockHash.ToString(),
                    fromAddr = receipt.Sender?.ToString() ?? string.Empty,
                    logsBloom = receipt.Bloom.ToString(),
                    gasUsed = receipt.GasUsed,
                    contractAddress = receipt.ContractAddress?.ToString() ?? string.Empty,
                    transactionHash = receipt.TxHash.ToString(),
                    cumulativeGasUsed = receipt.GasUsedTotal,
                    status = receipt.StatusCode,
                    logs = receipt.Logs?.Select((l, i) => new Log
                    {
                        logIndex = i,
                        blockNumber = (long) receipt.BlockNumber,
                        transactionIndex = receipt.Index,
                        blockHash = receipt.BlockHash.ToString(),
                        data = l.Data.ToString(),
                        transactionHash = receipt.TxHash.ToString(),
                        address = l.LoggersAddress.ToString(),
                        logTopics = l.Topics?.Select(t => t.ToString()).ToList() ?? new List<string>(),
                        removed = removed
                    }).ToList() ?? new List<Log>()
                },
                tx = MapTransaction(index, receipt.BlockNumber, receipt.BlockHash, transaction)
            };
        }
    }
}