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
using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Core.Encoding
{
    public class TransactionDecoder : IRlpDecoder<Transaction>
    {
        internal Transaction Decode(object[] data)
        {
            if (data.Length != 6 && data.Length != 9)
            {
                throw new RlpException($"{nameof(Transaction)} expected to have 6 or 9 elements");
            }

            Transaction transaction = new Transaction();
            transaction.Nonce = ((byte[])data[0]).ToUnsignedBigInteger();
            transaction.GasPrice = ((byte[])data[1]).ToUnsignedBigInteger();
            transaction.GasLimit = ((byte[])data[2]).ToUnsignedBigInteger();
            byte[] toData = (byte[])data[3];
            transaction.To = toData.Length == 0 ? null : new Address(toData);
            transaction.Value = ((byte[])data[4]).ToUnsignedBigInteger();
            if (transaction.To == null)
            {
                transaction.Init = (byte[])data[5];
            }
            else
            {
                transaction.Data = (byte[])data[5];
            }

            if (data.Length > 6)
            {
                // either eip155 or signed
                byte[] vBytes = (byte[])data[6];
                byte[] rBytes = (byte[])data[7];
                byte[] sBytes = (byte[])data[8];

                if (vBytes[0] == 0 || rBytes[0] == 0 || sBytes[0] == 0)
                {
                    throw new RlpException("VRS starting with 0");
                }

                if (rBytes.Length > 32 || sBytes.Length > 32)
                {
                    throw new RlpException("R and S lengths expected to be less or equal 32");
                }

                byte v = vBytes[0]; // TODO: support higher chain IDs
                BigInteger r = rBytes.ToUnsignedBigInteger();
                BigInteger s = sBytes.ToUnsignedBigInteger();

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
            object[] data = (object[])Rlp.Decode(rlp);
            return Decode(data);
        }
    }
}