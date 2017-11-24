using System;
using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Core.Encoding
{
    public class TransactionDecoder : IRlpDecoder<Transaction>
    {
        internal Transaction Decode(object[] data)
        {
            Transaction transaction = new Transaction();
            transaction.Nonce = ((byte[]) data[0]).ToUnsignedBigInteger();
            transaction.GasPrice = ((byte[]) data[1]).ToUnsignedBigInteger();
            transaction.GasLimit = ((byte[]) data[2]).ToUnsignedBigInteger();
            byte[] toData = (byte[]) data[3];
            transaction.To = toData.Length == 0 ? null : new Address(toData);
            transaction.Value = ((byte[]) data[4]).ToUnsignedBigInteger();
            if (transaction.To == null)
            {
                transaction.Init = (byte[]) data[5];
            }
            else
            {
                transaction.Data = (byte[]) data[5];
            }

            if (data.Length > 6)
            {
                // either eip155 or signed
                byte v = ((byte[]) data[6])[0];
                BigInteger r = ((byte[]) data[7]).ToUnsignedBigInteger();
                BigInteger s = ((byte[]) data[8]).ToUnsignedBigInteger();

                if (s == BigInteger.Zero && r == BigInteger.Zero)
                {
                    throw new InvalidOperationException();
                }

                Signature signature = new Signature(r, s, v);
                transaction.Signature = signature;
            }

            return transaction;
        }
        
        public Transaction Decode(Rlp rlp)
        {
            object[] data = (object[]) Rlp.Decode(rlp);
            return Decode(data);
        }
    }
}