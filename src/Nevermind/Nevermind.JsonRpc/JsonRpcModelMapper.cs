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
            return new Block
            {
                Number = new Quantity(block.Header.Number),
                Hash = new Data(block.Hash.Bytes),
                ParentHash = new Data(block.Header.ParentHash.Bytes),
                Nonce = new Data(new Hex(block.Header.Nonce.ToString())),
                Sha3Uncles = new Data(block.Header.OmmersHash.Bytes),
                LogsBloom = new Data(block.Header.Bloom.Bytes),
                TransactionsRoot = new Data(block.Header.TransactionsRoot.Bytes),
                StateRoot = new Data(block.Header.StateRoot.Bytes),
                ReceiptsRoot = new Data(block.Header.ReceiptsRoot.Bytes),
                Miner = new Data(block.Header.Beneficiary.Hex),
                Difficulty = new Quantity(block.Header.Difficulty),
                //TotalDifficulty = new Quantity(block.Header.Difficulty),
                ExtraData = new Data(block.Header.ExtraData),
                //Size = new Quantity(block.Header.)
                GasLimit = new Quantity(block.Header.GasLimit),
                GasUsed = new Quantity(block.Header.GasUsed),
                Timestamp = new Quantity(block.Header.Timestamp),
                Uncles = block.Ommers.Select(x => new Data(x.Hash.Bytes)).ToArray(),
                Transactions = returnFullTransactionObjects ? block.Transactions.Select(x => MapTransaction(x, block)).ToArray() : null,
                TransactionHashes = !returnFullTransactionObjects ? block.Transactions.Select(x => new Data(x.Hash.Bytes)).ToArray() : null
            };
        }

        public Transaction MapTransaction(Core.Transaction transaction, Core.Block block)
        {
            return new Transaction
            {
                Hash = new Data(transaction.Hash.Bytes),
                Nonce = new Quantity(transaction.Nonce),
                BlockHash = block != null ? new Data(block.Hash.Bytes) : null,
                BlockNumber = block != null ? new Quantity(block.Header.Number) : null,
                TransactionIndex = block != null ? new Quantity(GetTransactionIndex(transaction, block)) : null,
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
                TransactionIndex = block != null ? new Quantity(GetTransactionIndex(transaction, block)) : null,
                BlockHash = block != null ? new Data(block.Hash.Bytes) : null,
                BlockNumber = block != null ? new Quantity(block.Header.Number) : null,
                //CumulativeGasUsed = new Quantity(receipt.GasUsed),
                GasUsed = new Quantity(receipt.GasUsed),
                ContractAddress = transaction.IsContractCreation ? new Data(receipt.Recipient.Hex) : null,
                Logs = receipt.Logs.Select(MapLog).ToArray(),
                LogsBloom = new Data(receipt.Bloom.Bytes),
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