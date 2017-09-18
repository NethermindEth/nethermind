using System;
using System.Numerics;

namespace Nevermind.Core.Encoding
{
    public class Rlp
    {
        public byte[] Bytes { get; }

        public Rlp(byte[] bytes)
        {
            Bytes = bytes;
        }

        public static Rlp Encode(BigInteger bigInteger)
        {
            throw new NotImplementedException();
        }

        public static Rlp Encode(Account account)
        {
            return new Rlp(
                RecursiveLengthPrefix.Serialize(account.Nonce, account.Balance, account.StorageRoot, account.CodeHash));
        }

        public static Rlp Encode(TransactionReceipt receipt)
        {
            return new Rlp(
                RecursiveLengthPrefix.Serialize(receipt.PostTransactionState, receipt.GasUsed, receipt.Bloom, receipt.Logs));
        }

        public static Rlp Encode(Transaction transaction)
        {
            throw new NotImplementedException();
        }
    }
}