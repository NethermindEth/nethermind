// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class TransactionInfoDecoder : IRlpNdmDecoder<TransactionInfo>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static TransactionInfoDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(TransactionInfo)] = new TransactionInfoDecoder();
        }

        public TransactionInfo Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            try
            {
                Keccak hash = rlpStream.DecodeKeccak();
                UInt256 value = rlpStream.DecodeUInt256();
                UInt256 gasPrice = rlpStream.DecodeUInt256();
                ulong gasLimit = rlpStream.DecodeUlong();
                ulong timestamp = rlpStream.DecodeUlong();
                TransactionType type = (TransactionType)rlpStream.DecodeInt();
                TransactionState state = (TransactionState)rlpStream.DecodeInt();

                return new TransactionInfo(hash, value, gasPrice, gasLimit, timestamp, type, state);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(TransactionInfo)} could not be decoded", e);
            }
        }

        public void Encode(RlpStream stream, TransactionInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(TransactionInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Hash),
                Serialization.Rlp.Rlp.Encode(item.Value),
                Serialization.Rlp.Rlp.Encode(item.GasPrice),
                Serialization.Rlp.Rlp.Encode(item.GasLimit),
                Serialization.Rlp.Rlp.Encode(item.Timestamp),
                Serialization.Rlp.Rlp.Encode((int)item.Type),
                Serialization.Rlp.Rlp.Encode((int)item.State));
        }

        public int GetLength(TransactionInfo item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
