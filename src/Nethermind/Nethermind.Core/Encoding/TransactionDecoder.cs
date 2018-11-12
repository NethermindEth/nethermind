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

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Encoding
{
    public class TransactionDecoder : IRlpDecoder<Transaction>
    {
        public Transaction Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var transactionSequence = context.PeekNextItem();

            int transactionLength = context.ReadSequenceLength();
            int lastCheck = context.Position + transactionLength;
            Transaction transaction = new Transaction();
            transaction.Nonce = context.DecodeUInt256();
            transaction.GasPrice = context.DecodeUInt256();
            transaction.GasLimit = context.DecodeUInt256();
            transaction.To = context.DecodeAddress();
            transaction.Value = context.DecodeUInt256();
            if (transaction.To == null)
            {
                transaction.Init = context.DecodeByteArray();
            }
            else
            {
                transaction.Data = context.DecodeByteArray();
            }

            if (context.Position < lastCheck)
            {
                byte[] vBytes = context.DecodeByteArray();
                byte[] rBytes = context.DecodeByteArray();
                byte[] sBytes = context.DecodeByteArray();

                if (vBytes[0] == 0 || rBytes[0] == 0 || sBytes[0] == 0)
                {
                    throw new RlpException("VRS starting with 0");
                }

                if (rBytes.Length > 32 || sBytes.Length > 32)
                {
                    throw new RlpException("R and S lengths expected to be less or equal 32");
                }

                int v = vBytes.ToInt32();
                BigInteger r = rBytes.ToUnsignedBigInteger();
                BigInteger s = sBytes.ToUnsignedBigInteger();

                if (s.IsZero && r.IsZero)
                {
                    throw new RlpException("Both 'r' and 's' are zero when decoding a transaction.");
                }

                Signature signature = new Signature(r, s, v);
                transaction.Signature = signature;
                transaction.Hash = Keccak.Compute(transactionSequence);
            }

            if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData))
            {
                context.Check(lastCheck);
            }

            return transaction;
        }

        public Rlp Encode(Transaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Rlp.Encode(item, false);
        }
    }
}