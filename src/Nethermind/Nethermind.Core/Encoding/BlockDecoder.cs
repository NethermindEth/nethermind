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

using System.Collections.Generic;
using System.IO;

namespace Nethermind.Core.Encoding
{
    public class BlockDecoder : IRlpDecoder<Block>
    {
        private HeaderDecoder _headerDecoder = new HeaderDecoder();
        private TransactionDecoder _txDecoder = new TransactionDecoder();
        
        public Block Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (context.IsNextItemNull())
            {
                return null;
            }
            
            int sequenceLength = context.ReadSequenceLength();
            int blockCheck = context.Position + sequenceLength;

            BlockHeader header = Rlp.Decode<BlockHeader>(context);

            int transactionsSequenceLength = context.ReadSequenceLength();
            int transactionsCheck = context.Position + transactionsSequenceLength;
            List<Transaction> transactions = new List<Transaction>();
            while (context.Position < transactionsCheck)
            {
                transactions.Add(Rlp.Decode<Transaction>(context));
            }

            context.Check(transactionsCheck);

            int ommersSequenceLength = context.ReadSequenceLength();
            int ommersCheck = context.Position + ommersSequenceLength;
            List<BlockHeader> ommerHeaders = new List<BlockHeader>();
            while (context.Position < ommersCheck)
            {
                ommerHeaders.Add(Rlp.Decode<BlockHeader>(context, rlpBehaviors));
            }

            context.Check(ommersCheck);

            if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData))
            {
                context.Check(blockCheck);
            }

            return new Block(header, transactions, ommerHeaders);
        }

        public Rlp Encode(Block item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }
            
            return Rlp.Encode(
                Rlp.Encode(item.Header),
                Rlp.Encode(item.Transactions),
                Rlp.Encode(item.Ommers));
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

        public void Encode(MemoryStream stream, Block item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.Write(Rlp.OfEmptySequence.Bytes);
                return;
            }
            
            (int contentLength, int txsLength, int ommersLength) = GetContentLength(item, rlpBehaviors);
            Rlp.StartSequence(stream, contentLength);
            _headerDecoder.Encode(stream, item.Header);
            Rlp.StartSequence(stream, txsLength);
            for (int i = 0; i < item.Transactions.Length; i++)
            {
                _txDecoder.Encode(stream, item.Transactions[i]);
            }
            
            Rlp.StartSequence(stream, ommersLength);
            for (int i = 0; i < item.Ommers.Length; i++)
            {
                _headerDecoder.Encode(stream, item.Ommers[i]);
            }
        }

        public int GetLength(Block item, RlpBehaviors rlpBehaviors)
        {
            if (item == null)
            {
                return 1;
            }
            
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);
        }
    }
}