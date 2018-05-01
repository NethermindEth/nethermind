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
        private readonly BlockHeaderDecoder _blockHeaderDecoder = new BlockHeaderDecoder();
        
        public Block Decode(Rlp rlp)
        {
            DecodedRlp data = Rlp.Decode(rlp);
            return Decode(data);
        }

        public Block Decode(DecodedRlp data)
        {
            if (data == null)
            {
                return null;
            }
            
            DecodedRlp headerData = data.GetSequence(0);
            DecodedRlp transactionsData = data.GetSequence(1);
            DecodedRlp ommersData = data.GetSequence(2);

            BlockHeader blockHeader = Rlp.Decode<BlockHeader>(headerData);

            List<Transaction> transactions = new List<Transaction>();
            for (int txIndex = 0; txIndex < transactionsData.Items.Count; txIndex++)
            {
                DecodedRlp transactionData = (DecodedRlp)transactionsData.Items[txIndex];
                transactions.Add(Rlp.Decode<Transaction>(transactionData));
            }

            BlockHeader[] ommers = new BlockHeader[ommersData.Length];
            for (int ommerIndex = 0; ommerIndex < ommersData.Length; ommerIndex++)
            {
                ommers[ommerIndex] = Rlp.Decode<BlockHeader>(ommersData.GetSequence(ommerIndex));
            }

            Block block = new Block(blockHeader, ommers);
            block.Transactions = transactions.ToArray();
            return block;
        }
    }
}