﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp
{
    public class BlockDecoder : IRlpDecoder<Block>, IRlpValueDecoder<Block>
    {
        private HeaderDecoder _headerDecoder = new HeaderDecoder();
        private TransactionDecoder _txDecoder = new TransactionDecoder();
        
        public Block Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }
            
            int sequenceLength = rlpStream.ReadSequenceLength();
            int blockCheck = rlpStream.Position + sequenceLength;

            BlockHeader header = Rlp.Decode<BlockHeader>(rlpStream);

            int transactionsSequenceLength = rlpStream.ReadSequenceLength();
            int transactionsCheck = rlpStream.Position + transactionsSequenceLength;
            List<Transaction> transactions = new List<Transaction>();
            while (rlpStream.Position < transactionsCheck)
            {
                transactions.Add(Rlp.Decode<Transaction>(rlpStream));
            }

            rlpStream.Check(transactionsCheck);

            int ommersSequenceLength = rlpStream.ReadSequenceLength();
            int ommersCheck = rlpStream.Position + ommersSequenceLength;
            List<BlockHeader> ommerHeaders = new List<BlockHeader>();
            while (rlpStream.Position < ommersCheck)
            {
                ommerHeaders.Add(Rlp.Decode<BlockHeader>(rlpStream, rlpBehaviors));
            }

            rlpStream.Check(ommersCheck);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(blockCheck);
            }

            return new Block(header, transactions, ommerHeaders);
        }

        private (int Total, int Txs, int Ommers) GetContentLength(Block item, RlpBehaviors rlpBehaviors)
        {   
            int contentLength = _headerDecoder.GetLength(item.Header, rlpBehaviors);
            
            int txLength = GetTxLength(item, rlpBehaviors);
            contentLength += Rlp.GetSequenceRlpLength(txLength);

            int ommersLength = GetOmmersLength(item, rlpBehaviors);
            contentLength += Rlp.GetSequenceRlpLength(ommersLength);

            return (contentLength, txLength, ommersLength);
        }

        private int GetOmmersLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int ommersLength = 0;
            for (int i = 0; i < item.Ommers.Length; i++)
            {
                ommersLength += _headerDecoder.GetLength(item.Ommers[i], rlpBehaviors);
            }

            return ommersLength;
        }

        private int GetTxLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int txLength = 0;
            for (int i = 0; i < item.Transactions.Length; i++)
            {
                txLength += _txDecoder.GetLength(item.Transactions[i], rlpBehaviors);
            }

            return txLength;
        }

        public int GetLength(Block item, RlpBehaviors rlpBehaviors)
        {
            if (item == null)
            {
                return 1;
            }
            
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);
        }

        public Block Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }
            
            int sequenceLength = decoderContext.ReadSequenceLength();
            int blockCheck = decoderContext.Position + sequenceLength;

            BlockHeader header = Rlp.Decode<BlockHeader>(ref decoderContext);

            int transactionsSequenceLength = decoderContext.ReadSequenceLength();
            int transactionsCheck = decoderContext.Position + transactionsSequenceLength;
            List<Transaction> transactions = new List<Transaction>();
            while (decoderContext.Position < transactionsCheck)
            {
                transactions.Add(Rlp.Decode<Transaction>(ref decoderContext));
            }

            decoderContext.Check(transactionsCheck);

            int ommersSequenceLength = decoderContext.ReadSequenceLength();
            int ommersCheck = decoderContext.Position + ommersSequenceLength;
            List<BlockHeader> ommerHeaders = new List<BlockHeader>();
            while (decoderContext.Position < ommersCheck)
            {
                ommerHeaders.Add(Rlp.Decode<BlockHeader>(ref decoderContext, rlpBehaviors));
            }

            decoderContext.Check(ommersCheck);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                decoderContext.Check(blockCheck);
            }

            return new Block(header, transactions, ommerHeaders);
        }

        public Rlp Encode(Block item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }
            
            RlpStream rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }
        
        public void Encode(RlpStream stream, Block item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.EncodeNullObject();
                return;
            }
            
            (int contentLength, int txsLength, int ommersLength) = GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            stream.Encode(item.Header);
            stream.StartSequence(txsLength);
            for (int i = 0; i < item.Transactions.Length; i++)
            {
                stream.Encode(item.Transactions[i]);
            }
            
            stream.StartSequence(ommersLength);
            for (int i = 0; i < item.Ommers.Length; i++)
            {
                stream.Encode(item.Ommers[i]);
            }
        }
    }
}