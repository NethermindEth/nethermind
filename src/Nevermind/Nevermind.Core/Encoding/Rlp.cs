using System;
using System.Numerics;

namespace Nevermind.Core.Encoding
{
    public partial class Rlp
    {
        public byte[] Bytes { get; }

        public byte this[int index] => Bytes[index];
        public int Length => Bytes.Length;

        public Rlp(byte singleByte)
        {
            Bytes = new[] { singleByte };
        }

        public Rlp(byte[] bytes)
        {
            Bytes = bytes;
        }

        public static Rlp Encode(BlockHeader header)
        {
            return Serialize(
                    header.ParentHash,
                    header.OmmersHash,
                    header.Beneficiary,
                    header.StateRoot,
                    header.TransactionsRoot,
                    header.ReceiptsRoot,
                    header.LogsBloom,
                    header.Difficulty,
                    header.Number,
                    header.GasLimit,
                    header.GasUsed,
                    header.Timestamp,
                    header.ExtraData,
                    header.MixHash,
                    header.Nonce
                    );
        }

        public static Rlp Encode(Block block)
        {
            return Serialize(block.Header, block.Transactions, block.Ommers);
        }

        public static Rlp Encode(BigInteger bigInteger)
        {
            throw new NotImplementedException();
        }

        public static Rlp Encode(Account account)
        {
            return Serialize(account.Nonce, account.Balance, account.StorageRoot, account.CodeHash);
        }

        public static Rlp Encode(TransactionReceipt receipt)
        {
            return Serialize(receipt.PostTransactionState, receipt.GasUsed, receipt.Bloom, receipt.Logs);
        }

        public static Rlp Encode(Transaction transaction)
        {
            throw new NotImplementedException();
        }

        public string ToString(bool withZeroX)
        {
            return Hex.FromBytes(Bytes, withZeroX);
        }

        public override string ToString()
        {
            return ToString(true);
        }
    }
}