using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class BlockConstructor
    {
        public Block GetBlockWithBeneficiaryBlockNumberAndTxInfo(Address beneficiary, int blockNumber, string[][] txInfo)
        {
            Transaction[] transactions = GetTransactionsFromTxStrings(txInfo, false);
            return Build.A.Block.WithBeneficiary(beneficiary).WithNumber(blockNumber).WithTransactions(transactions)
                .TestObject;
        }

        public static string[][] CollectTxStrings(params string[][] txInfo)
        {
            return txInfo;
        }

        public static string[] GetTxString(string privateKeyLetter, string gasPrice, string nonce)
        {
            return new[] {privateKeyLetter, gasPrice, nonce};
        }

        public static KeyValuePair<int, string[][]> BlockNumberAndTxStringsKeyValuePair(int blockNumber, string[][] txInfo)
        {
            return new KeyValuePair<int, string[][]>(blockNumber, txInfo);
        }

        public Block[] GetBlocksFromKeyValuePairs(params KeyValuePair<int, string[][]>[] blockAndTxInfo)
        {
            Keccak parentHash = null;
            Block block;
            List<Block> blocks = new List<Block>();
            foreach (KeyValuePair<int, string[][]> keyValuePair in blockAndTxInfo)
            {
                block = BlockBuilder(keyValuePair, parentHash);
                parentHash = block.Hash;
                blocks.Add(block);
            }

            return blocks.ToArray();
        }

        private Block BlockBuilder(KeyValuePair<int, string[][]> keyValuePair, Keccak parentHash, bool isEip1559 = false)
        {
            Transaction[] transactions;
            Block block;
            
            int blockNumber = keyValuePair.Key;
            string[][] txInfoArray = keyValuePair.Value;
            transactions = GetTransactionsFromTxStrings(txInfoArray, isEip1559);
            block = GetBlockWithNumberParentHashAndTxInfo(blockNumber, parentHash, transactions);
            return block;
        }

        public Transaction[] GetTransactionsFromTxStrings(string[][] txInfo, bool isEip1559)
        {
            if (txInfo == null)
            {
                return Array.Empty<Transaction>();
            }
            else if (isEip1559 == true)
            {
                return Enumerable.ToArray<Transaction>(ConvertEip1559Txs(txInfo));
            }
            else
            {
                return Enumerable.ToArray<Transaction>(ConvertRegularTxs(txInfo));
            }
        }

        private IEnumerable<Transaction> ConvertEip1559Txs(params string[][] txsInfo)
        {
            PrivateKey privateKey;
            char privateKeyLetter;
            UInt256 gasPrice;
            UInt256 nonce;
            foreach (string[] txInfo in txsInfo)
            {
                privateKeyLetter = Convert.ToChar(txInfo[0]);
                privateKey = PrivateKeyForLetter(privateKeyLetter);
                gasPrice = UInt256.Parse(txInfo[1]);
                nonce = UInt256.Parse(txInfo[2]);
                yield return Build.A.Transaction.SignedAndResolved(privateKey).WithGasPrice(gasPrice).WithNonce(nonce)
                    .WithType(TxType.EIP1559).TestObject;
            }
        }

        private Transaction[] ConvertRegularTxs(params string[][] txsInfo)
        {
            PrivateKey privateKey;
            char privateKeyLetter;
            UInt256 gasPrice;
            UInt256 nonce;
            Transaction transaction;
            List<Transaction> transactions = new List<Transaction>();
            foreach (string[] txInfo in txsInfo)
            {
                privateKeyLetter = Convert.ToChar(txInfo[0]);
                privateKey = PrivateKeyForLetter(privateKeyLetter);
                gasPrice = UInt256.Parse(txInfo[1]);
                nonce = UInt256.Parse(txInfo[2]);
                transaction = Build.A.Transaction.SignedAndResolved(privateKey).WithGasPrice(gasPrice).WithNonce(nonce)
                    .TestObject;
                transactions.Add(transaction);
            }

            return transactions.ToArray();
        }

        public static PrivateKey PrivateKeyForLetter(char privateKeyLetter)
        {
            switch (privateKeyLetter)
            {
                case 'A':
                    return TestItem.PrivateKeyA;
                case 'B':
                    return TestItem.PrivateKeyB;
                case 'C':
                    return TestItem.PrivateKeyC;
                case 'D':
                    return TestItem.PrivateKeyD;
                default:
                    throw new ArgumentException("PrivateKeyLetter should only be either A, B, C, or D.");
            }
        }

        public static Block GetBlockWithNumberParentHashAndTxInfo(int number, Keccak parentHash, Transaction[] txs)
        {
            if (number == 0)
            {
                return Build.A.Block.Genesis.WithTransactions(txs).TestObject;
            }

            else if (number > 0)
            {
                return Build.A.Block.WithNumber(number).WithParentHash(parentHash).WithTransactions(txs).TestObject;
            }
            
            else
            {
                throw new ArgumentException("Block number should be greater than or equal to 0.");
            }
        }
    }
}
