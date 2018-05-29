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
    public class BlockInfoDecoder : IRlpDecoder<BlockInfo>
    {
        public BlockInfo Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int lastCheck = context.ReadSequenceLength() + context.Position;

            BlockInfo blockInfo = new BlockInfo();
            blockInfo.BlockHash = context.DecodeKeccak();
            blockInfo.WasProcessed = context.DecodeBool();
            blockInfo.TotalDifficulty = context.DecodeUBigInt();
            blockInfo.TotalTransactions = context.DecodeUBigInt();

            if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData))
            {
                context.Check(lastCheck);
            }

            return blockInfo;
        }

        public Rlp Encode(BlockInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            Rlp[] elements = new Rlp[4];
            elements[0] = Rlp.Encode(item.BlockHash);
            elements[1] = Rlp.Encode(item.WasProcessed);
            elements[2] = Rlp.Encode(item.TotalDifficulty);
            elements[3] = Rlp.Encode(item.TotalTransactions);
            return Rlp.Encode(elements);
        }
    }
}