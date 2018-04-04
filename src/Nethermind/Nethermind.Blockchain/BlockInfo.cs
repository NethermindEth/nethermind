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
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public class BlockInfo
    {
        public BlockInfo(Keccak hash, BigInteger number)
        {
            BodyLocation = BlockDataLocation.Remote;
            HeaderLocation = BlockDataLocation.Remote;
            BlockQuality = Quality.Unknown;
            HeaderQuality = Quality.Unknown;
            Hash = hash;
            Number = number;
        }

        public BlockInfo(Block block)
        {
            BodyLocation = BlockDataLocation.Memory;
            HeaderLocation = BlockDataLocation.Memory;
            BlockQuality = Quality.Unknown;
            HeaderQuality = Quality.Unknown;
            Hash = block.Hash;
            Number = block.Number;
            Block = block;
        }

        public BlockDataLocation BodyLocation { get; set; }
        public BlockDataLocation HeaderLocation { get; set; }
        public Quality HeaderQuality { get; set; }
        public Quality BlockQuality { get; set; }
        public Keccak Hash { get; set; }
        public BigInteger Number { get; set; }
        public Block Block { get; set; }
        public BlockHeader BlockHeader { get; set; }
        public PublicKey ReceivedFrom { get; set; }
    }
}