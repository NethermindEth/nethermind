using System;
using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Core.Encoding
{
    public partial class Rlp : IEquatable<Rlp>
    {
        public static readonly Rlp OfEmptyByteArray = new Rlp(128);

        public static Rlp OfEmptySequence = Encode();

        private static readonly Dictionary<RuntimeTypeHandle, IRlpDecoder> Decoders =
            new Dictionary<RuntimeTypeHandle, IRlpDecoder>
            {
                [typeof(Transaction).TypeHandle] = new TransactionDecoder(),
                [typeof(Account).TypeHandle] = new AccountDecoder()
            };

        public Rlp(byte singleByte)
        {
            Bytes = new[] { singleByte };
        }

        public Rlp(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public byte this[int index] => Bytes[index];
        public int Length => Bytes.Length;

        public bool Equals(Rlp other)
        {
            if (other == null)
            {
                return false;
            }

            return Sugar.Bytes.UnsafeCompare(Bytes, other.Bytes);
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

        public static T Decode<T>(Rlp rlp)
        {
            if (Decoders.ContainsKey(typeof(T).TypeHandle))
            {
                return ((IRlpDecoder<T>)Decoders[typeof(T).TypeHandle]).Decode(rlp);
            }

            throw new NotImplementedException();
        }

        public static Rlp Encode(Transaction transaction, bool forSigning, bool eip155 = false, int chainId = 0)
        {
            object[] sequence = new object[forSigning && !eip155 ? 6 : 9];
            sequence[0] = transaction.Nonce;
            sequence[1] = transaction.GasPrice;
            sequence[2] = transaction.GasLimit;
            sequence[3] = transaction.To;
            sequence[4] = transaction.Value;
            sequence[5] = transaction.To == null ? transaction.Init : transaction.Data;

            if (forSigning)
            {
                if (eip155)
                {
                    sequence[6] = chainId;
                    sequence[7] = BigInteger.Zero;
                    sequence[8] = BigInteger.Zero;
                }
            }
            else
            {
                sequence[6] = transaction.Signature?.V;
                sequence[7] = transaction.Signature?.R;
                sequence[8] = transaction.Signature?.S;
            }

            return Encode(sequence);
        }

        public static Rlp Encode(Transaction transaction)
        {
            return Encode(transaction, false);
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
            if (address == null)
            {
                return OfEmptyByteArray;
            }

            byte[] result = new byte[21];
            result[0] = 148;
            Buffer.BlockCopy(address.Hex, 0, result, 1, 20);
            return new Rlp(result);
        }

        public string ToString(bool withZeroX)
        {
            return Hex.FromBytes(Bytes, withZeroX);
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