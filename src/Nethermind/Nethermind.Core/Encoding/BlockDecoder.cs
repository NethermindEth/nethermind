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

namespace Nethermind.Core.Encoding
{
    public class BlockDecoder : IRlpDecoder<Block>
    {
        public Block Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
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
            return Rlp.Encode(
                Rlp.Encode(item.Header),
                Rlp.Encode(item.Transactions),
                Rlp.Encode(item.Ommers));
        }
    }
}