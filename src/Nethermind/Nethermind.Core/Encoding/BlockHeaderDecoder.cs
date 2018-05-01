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
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Encoding
{
    public class BlockHeaderDecoder : IRlpDecoder<BlockHeader>
    {
        public BlockHeader Decode(DecodedRlp data)
        {
            if (data == null)
            {
                return null;
            }
            
            Keccak parentHash = data.GetKeccak(0);
            Keccak ommersHash = data.GetKeccak(1);
            Address beneficiary = data.GetAddress(2);
            Keccak stateRoot = data.GetKeccak(3);
            Keccak transactionsRoot = data.GetKeccak(4);
            Keccak receiptsRoot = data.GetKeccak(5);
            byte[] bloomBytes = data.GetBytes(6);
            Bloom bloom = bloomBytes.Length == 256 ? new Bloom(bloomBytes.ToBigEndianBitArray2048()) : throw new InvalidOperationException("Incorrect bloom RLP");
            BigInteger difficulty = data.GetUnsignedBigInteger(7);
            BigInteger number = data.GetUnsignedBigInteger(8);
            BigInteger gasLimit = data.GetUnsignedBigInteger(9);
            BigInteger gasUsed = data.GetUnsignedBigInteger(10);
            BigInteger timestamp = data.GetUnsignedBigInteger(11);
            byte[] extraData = data.GetBytes(12);
            Keccak mixHash = data.GetKeccak(13);
            BigInteger nonce = data.GetUnsignedBigInteger(14);

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
            blockHeader.Hash = BlockHeader.CalculateHash(blockHeader);
            return blockHeader;
        }

        public BlockHeader Decode(Rlp rlp)
        {
            DecodedRlp header = Rlp.Decode(rlp);
            return Decode(header);
        }
    }
}