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
    public class NewHeaderDecoder : INewRlpDecoder<BlockHeader>
    {
        public BlockHeader Decode(NewRlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            byte[] headerRlp = context.ReadSequenceRlp();
            context.Position -= headerRlp.Length;

            long headerSequenceLength = context.ReadSequenceLength();
            long headerCheck = context.Position + headerSequenceLength;

            Keccak parentHash = context.ReadKeccak();
            Keccak ommersHash = context.ReadKeccak();
            Address beneficiary = context.ReadAddress();
            Keccak stateRoot = context.ReadKeccak();
            Keccak transactionsRoot = context.ReadKeccak();
            Keccak receiptsRoot = context.ReadKeccak();
            Bloom bloom = context.DecodeBloom();
            BigInteger difficulty = context.ReadUBigInt();
            BigInteger number = context.ReadUBigInt();
            BigInteger gasLimit = context.ReadUBigInt();
            BigInteger gasUsed = context.ReadUBigInt();
            BigInteger timestamp = context.ReadUBigInt();
            byte[] extraData = context.ReadByteArray();
            Keccak mixHash = context.ReadKeccak();
            BigInteger nonce = context.ReadUBigInt();

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
    }
}