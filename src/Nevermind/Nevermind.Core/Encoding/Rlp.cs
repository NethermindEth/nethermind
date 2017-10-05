using System;
using Nevermind.Core.Sugar;

namespace Nevermind.Core.Encoding
{
    public partial class Rlp : IEquatable<Rlp>
    {
        public static readonly Rlp OfEmptyByteArray = new Rlp(128);

        public static Rlp OfEmptySequence = Encode();

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
            return Encode(
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
            return Encode(block.Header, block.Transactions, block.Ommers);
        }

        public static Rlp Encode(Bloom bloom)
        {
            byte[] result = new byte[259];
            result[0] = 185;
            result[1] = 1;
            result[2] = 0;
            Buffer.BlockCopy(bloom.Bytes, 0, result, 3, 256);
            return new Rlp(result);
        }

        public static Rlp Encode(Account account)
        {
            return Encode(
                account.Nonce,
                account.Balance,
                account.StorageRoot,
                account.CodeHash);
        }

        public static Rlp Encode(TransactionReceipt receipt)
        {
            return Encode(receipt.PostTransactionState, receipt.GasUsed, receipt.Bloom, receipt.Logs);
        }

        public static Rlp Encode(Transaction transaction)
        {
            throw new NotImplementedException();
        }

        public static Rlp Encode(Keccak keccak)
        {
            byte[] result = new byte[33];
            result[0] = 160;
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
            Buffer.BlockCopy(address.Hex, 0, result, 1, 20);
            return new Rlp(result);
        }

        public string ToString(bool withZeroX)
        {
            return Hex.FromBytes(Bytes, withZeroX);
        }

        public bool Equals(Rlp other)
        {
            if (other == null)
            {
                return false;
            }

            return Sugar.Bytes.UnsafeCompare(Bytes, other.Bytes);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public int GetHashCode(Rlp obj)
        {
            return obj.Bytes.GetXxHashCode();
        }
    }
}