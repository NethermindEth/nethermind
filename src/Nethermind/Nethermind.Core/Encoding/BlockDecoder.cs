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

namespace Nethermind.Core.Encoding
{
    public class BlockDecoder : IRlpDecoder<Block>
    {
        public Block Decode(Rlp rlp, bool ignoreBody)
        {
            DecodedRlp data = Rlp.Decode(rlp);
            return Decode(data, ignoreBody);
        }

        public Block Decode(Rlp rlp)
        {
            return Decode(rlp, false);
        }

        public Block Decode(DecodedRlp data)
        {
            return Decode(data, false);
        }

        public Block Decode(DecodedRlp data, bool ignoreBody)
        {
            if (data == null)
            {
                return null;
            }

            DecodedRlp headerData = data.GetSequence(0);
            BlockHeader blockHeader = Rlp.Decode<BlockHeader>(headerData);

            Transaction[] transactions = null;
            BlockHeader[] ommers = null;
            if (!ignoreBody)
            {
                DecodedRlp transactionsData = data.GetSequence(1);
                transactions = new Transaction[transactionsData.Items.Count];
                for (int txIndex = 0; txIndex < transactionsData.Items.Count; txIndex++)
                {
                    DecodedRlp transactionData = (DecodedRlp)transactionsData.Items[txIndex];
                    transactions[txIndex] = Rlp.Decode<Transaction>(transactionData);
                }

                DecodedRlp ommersData = data.GetSequence(2);
                ommers = new BlockHeader[ommersData.Length];
                for (int ommerIndex = 0; ommerIndex < ommersData.Length; ommerIndex++)
                {
                    ommers[ommerIndex] = Rlp.Decode<BlockHeader>(ommersData.GetSequence(ommerIndex));
                }
            }

            Block block = new Block(blockHeader, ommers ?? new BlockHeader[0]);
            block.Transactions = transactions ?? new Transaction[0];
            return block;
        }
    }
}