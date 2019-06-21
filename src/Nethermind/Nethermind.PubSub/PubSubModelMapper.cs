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
using Nethermind.PubSub.Models;

namespace Nethermind.PubSub
{
    public class PubSubModelMapper : IPubSubModelMapper
    {
        public Block MapBlock(Core.Block block)
            => block == null
                ? null
                : new Block
                {
                    Hash = block.Hash.Bytes,
                    Header = MapBlockHeader(block.Header),
                    Transactions = block.Transactions?.Select(MapTransaction).ToArray() ?? new Transaction[0],
                    Ommers = block.Ommers?.Select(MapBlockHeader).ToArray() ?? new BlockHeader[0],
                    ParentHash = block.ParentHash.Bytes,
                    Beneficiary = block.Beneficiary.Bytes,
                    StateRoot = block.StateRoot.Bytes,
                    TransactionsRoot = block.TransactionsRoot.Bytes,
                    GasLimit = block.GasLimit,
                    GasUsed = block.GasUsed,
                    Timestamp = block.Timestamp.ToString(),
                    Number = block.Number.ToString(),
                    Difficulty = block.Difficulty.ToString(),
                    TotalDifficulty = block.TotalDifficulty?.ToString()
                };

        public TransactionReceipt MapTransactionReceipt(Core.TxReceipt receipt)
            => receipt == null
                ? null
                : new TransactionReceipt
                {
                    StatusCode = receipt.StatusCode,
                    BlockNumber = receipt.BlockNumber.ToString(),
                    BlockHash = receipt.BlockHash?.Bytes,
                    TransactionHash = receipt.TxHash?.Bytes,
                    Index = receipt.Index,
                    GasUsed = receipt.GasUsed,
                    Sender = receipt.Sender?.Bytes,
                    ContractAddress = receipt.ContractAddress?.Bytes,
                    Recipient = receipt.Recipient?.Bytes,
                    PostTransactionState = receipt.PostTransactionState?.Bytes,
                    Bloom = receipt.Bloom?.Bytes,
                    Logs = receipt.Logs?.Select(MapLogEntry).ToArray() ?? new LogEntry[0],
                    Error = receipt.Error
                };

        public Transaction MapTransaction(Core.Transaction transaction)
            => transaction == null
                ? null
                : new Transaction
                {
                    Nonce = transaction.Nonce.ToString(),
                    GasPrice = transaction.GasPrice.ToString(),
                    GasLimit = transaction.GasLimit.ToString(),
                    To = transaction.To?.Bytes,
                    Value = transaction.Value.ToString(),
                    Data = transaction.Data,
                    Init = transaction.Init,
                    SenderAddress = transaction.SenderAddress?.Bytes,
                    Signature = MapSignature(transaction.Signature),
                    IsSigned = transaction.IsSigned,
                    IsContractCreation = transaction.IsContractCreation,
                    IsMessageCall = transaction.IsMessageCall,
                    Hash = transaction.Hash?.Bytes,
                    DeliveredBy = transaction.DeliveredBy?.Bytes
                };
        
        private static BlockHeader MapBlockHeader(Core.BlockHeader header)
            => header == null
                ? null
                : new BlockHeader
                {
                    ParentHash = header.ParentHash.Bytes,
                    OmmersHash = header.OmmersHash.Bytes,
                    Beneficiary = header.Beneficiary.Bytes,
                    StateRoot = header.StateRoot.Bytes,
                    TransactionsRoot = header.TxRoot.Bytes,
                    ReceiptsRoot = header.ReceiptsRoot.Bytes,
                    Bloom = header.Bloom.Bytes,
                    Difficulty = header.Difficulty.ToString(),
                    Number = header.Number.ToString(),
                    GasUsed = header.GasUsed,
                    GasLimit = header.GasLimit,
                    Timestamp = header.Timestamp.ToString(),
                    ExtraData = header.ExtraData,
                    MixHash = header.MixHash.Bytes,
                    Nonce = header.Nonce,
                    Hash = header.Hash.Bytes,
                    TotalDifficulty = header.TotalDifficulty?.ToString()
                };

        private static Signature MapSignature(Core.Crypto.Signature signature)
            => signature == null
                ? null
                : new Signature
                {
                    Bytes = signature.Bytes,
                    V = signature.V,
                    RecoveryId = signature.RecoveryId,
                    R = signature.R,
                    S = signature.S
                };

        private static LogEntry MapLogEntry(Core.LogEntry entry)
            => entry == null
                ? null
                : new LogEntry
                {
                    LoggersAddress = entry.LoggersAddress?.Bytes,
                    Topics = entry.Topics?.Select(t => t.Bytes).ToArray() ?? new Byte[0][],
                    Data = entry.Data
                };
    }
}