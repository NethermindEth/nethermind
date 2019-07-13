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
using System.IO;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

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
            UInt256 gasLimit = context.DecodeUInt256();
            transaction.GasLimit = gasLimit > new UInt256(long.MaxValue) ? long.MaxValue : (long)gasLimit;
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
                Span<byte> vBytes = context.DecodeByteArraySpan();
                Span<byte> rBytes = context.DecodeByteArraySpan();
                Span<byte> sBytes = context.DecodeByteArraySpan();

                if (vBytes[0] == 0 || rBytes[0] == 0 || sBytes[0] == 0)
                {
                    throw new RlpException("VRS starting with 0");
                }

                if (rBytes.Length > 32 || sBytes.Length > 32)
                {
                    throw new RlpException("R and S lengths expected to be less or equal 32");
                }

                int v = vBytes.ToInt32();

                if (rBytes.SequenceEqual(Bytes.Zero32) && sBytes.SequenceEqual(Bytes.Zero32))
                {
                    throw new RlpException("Both 'r' and 's' are zero when decoding a transaction.");
                }

                Signature signature = new Signature(rBytes, sBytes, v);
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

        public void Encode(MemoryStream stream, Transaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentLength = GetContentLength(item, false);
            Rlp.StartSequence(stream, contentLength);
            Rlp.Encode(stream, item.Nonce);
            Rlp.Encode(stream, item.GasPrice);
            Rlp.Encode(stream, item.GasLimit);
            Rlp.Encode(stream, item.To);
            Rlp.Encode(stream, item.Value);
            Rlp.Encode(stream, item.To == null ? item.Init : item.Data);
            Rlp.Encode(stream, item.Signature?.V ?? 0);
            Rlp.Encode(stream, item.Signature == null ? null : item.Signature.RAsSpan.WithoutLeadingZeros());
            Rlp.Encode(stream, item.Signature == null ? null : item.Signature.SAsSpan.WithoutLeadingZeros());
        }

        private int GetContentLength(Transaction item, bool forSigning , bool isEip155Enabled = false, int chainId = 0)
        {
            int contentLength = Rlp.LengthOf(item.Nonce)
                                + Rlp.LengthOf(item.GasPrice)
                                + Rlp.LengthOf(item.GasLimit)
                                + Rlp.LengthOf(item.To)
                                + Rlp.LengthOf(item.Value)
                                + (item.To == null ? Rlp.LengthOf(item.Init) : Rlp.LengthOf(item.Data));

            if (forSigning)
            {
                if (isEip155Enabled && chainId != 0)
                {
                    contentLength += Rlp.LengthOf(chainId);
                    contentLength += 1;
                    contentLength += 1;
                }
            }
            else
            {
                contentLength += item.Signature == null ? 1 : Rlp.LengthOf(item.Signature.V);
                contentLength += Rlp.LengthOf(item.Signature == null ? null : item.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += Rlp.LengthOf(item.Signature == null ? null : item.Signature.SAsSpan.WithoutLeadingZeros());
            }

            return contentLength;
        }

        public int GetLength(Transaction item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.GetSequenceRlpLength(GetContentLength(item, false));
        }
    }
}