using System;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Core.Encoding
{
    public partial class Rlp
    {
        public static Rlp OfEmptyString { get; } = new Rlp(128);

        public static Rlp OfEmptySequence => Serialize();

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

        public static Rlp Encode(Keccak keccak)
        {
            byte[] result = new byte[33];
            result[0] = 161;
            Buffer.BlockCopy(keccak.Bytes, 0, result, 1, 32);
            return new Rlp(result);
        }

        public static Keccak DecodeKeccak(Rlp rlp)
        {
            return new Keccak(rlp.Bytes.Slice(1, 32));
        }

        public static Rlp Encode(Address address)
        {
            byte[] result = new byte[21];
            result[0] = 148;
            Buffer.BlockCopy(address.Hex, 0, result, 1, 32);
            return new Rlp(result);
        }

        public static Rlp Encode(Rlp rlp)
        {
            return rlp;
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