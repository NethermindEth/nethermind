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

namespace Nethermind.Core.Encoding
{
    public class HeaderDecoder : IRlpDecoder<BlockHeader>
    {
        public BlockHeader Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            byte[] headerRlp = context.ReadSequenceRlp();
            context.Position -= headerRlp.Length;

            int headerSequenceLength = context.ReadSequenceLength();
            int headerCheck = context.Position + headerSequenceLength;

            Keccak parentHash = context.DecodeKeccak();
            Keccak ommersHash = context.DecodeKeccak();
            Address beneficiary = context.DecodeAddress();
            Keccak stateRoot = context.DecodeKeccak();
            Keccak transactionsRoot = context.DecodeKeccak();
            Keccak receiptsRoot = context.DecodeKeccak();
            Bloom bloom = context.DecodeBloom();
            BigInteger difficulty = context.DecodeUBigInt();
            BigInteger number = context.DecodeUBigInt();
            BigInteger gasLimit = context.DecodeUBigInt();
            BigInteger gasUsed = context.DecodeUBigInt();
            BigInteger timestamp = context.DecodeUBigInt();
            byte[] extraData = context.DecodeByteArray();
            Keccak mixHash = context.DecodeKeccak();
            BigInteger nonce = context.DecodeUBigInt();

            if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData))
            {
                context.Check(headerCheck);
            }

            BlockHeader blockHeader = new BlockHeader(
                parentHash,
                ommersHash,
                beneficiary,
                difficulty,
                number,
                (long)gasLimit,
                timestamp,
                extraData);

            blockHeader.StateRoot = stateRoot;
            blockHeader.TransactionsRoot = transactionsRoot;
            blockHeader.ReceiptsRoot = receiptsRoot;
            blockHeader.Bloom = bloom;
            blockHeader.GasUsed = (long)gasUsed;
            blockHeader.MixHash = mixHash;
            blockHeader.Nonce = (ulong)nonce;
            blockHeader.Hash = BlockHeader.CalculateHash(new Rlp(headerRlp));
            return blockHeader;
        }

        public Rlp Encode(BlockHeader item, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            bool withMixHashAndNonce = !behaviors.HasFlag(RlpBehaviors.ExcludeBlockMixHashAndNonce);
            int numberOfElements = withMixHashAndNonce ? 15 : 13;
            Rlp[] elements = new Rlp[numberOfElements];
            elements[0] = Rlp.Encode(item.ParentHash);
            elements[1] = Rlp.Encode(item.OmmersHash);
            elements[2] = Rlp.Encode(item.Beneficiary);
            elements[3] = Rlp.Encode(item.StateRoot);
            elements[4] = Rlp.Encode(item.TransactionsRoot);
            elements[5] = Rlp.Encode(item.ReceiptsRoot);
            elements[6] = Rlp.Encode(item.Bloom);
            elements[7] = Rlp.Encode(item.Difficulty);
            elements[8] = Rlp.Encode(item.Number);
            elements[9] = Rlp.Encode(item.GasLimit);
            elements[10] = Rlp.Encode(item.GasUsed);
            elements[11] = Rlp.Encode(item.Timestamp);
            elements[12] = Rlp.Encode(item.ExtraData);
            if (withMixHashAndNonce)
            {
                elements[13] = Rlp.Encode(item.MixHash);
                elements[14] = Rlp.Encode(item.Nonce);
            }

            return Rlp.Encode(elements);
        }
    }
}