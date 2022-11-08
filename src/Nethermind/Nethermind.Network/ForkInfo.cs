//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Buffers.Binary;
using System.Linq;
using Force.Crc32;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Network
{
    public static class ForkInfo
    {
        private const long ImpossibleBlockNumberWithSpaceForImpossibleForks = long.MaxValue - 100;
        private const ulong ImpossibleTimestampWithSpaceForImpossibleForks = ulong.MaxValue - 100;

        public static byte[] CalculateForkHash(ISpecProvider specProvider, long headNumber, ulong headTimestamp, Keccak genesisHash)
        {
            uint crc = 0;
            ForkActivation[] transitionBlocks = specProvider.TransitionBlocks;
            byte[] blockNumberBytes = new byte[8];
            crc = Crc32Algorithm.Append(crc, genesisHash.Bytes);
            for (int i = 0; i < transitionBlocks.Length; i++)
            {
                if (transitionBlocks[i] > (headNumber, headTimestamp))
                    break;
                ulong numberToAddToCrc = transitionBlocks[i].Timestamp ?? (ulong)transitionBlocks[i].BlockNumber;
                BinaryPrimitives.WriteUInt64BigEndian(blockNumberBytes, numberToAddToCrc);
                crc = Crc32Algorithm.Append(crc, blockNumberBytes);
            }

            byte[] forkHash = new byte[4];
            BinaryPrimitives.TryWriteUInt32BigEndian(forkHash, crc);
            return forkHash;
        }

        public static ForkId CalculateForkId(ISpecProvider specProvider, long headNumber, ulong headTimestamp, Keccak genesisHash)
        {

            byte[] forkHash = CalculateForkHash(specProvider, headNumber, headTimestamp, genesisHash);
            ulong next = 0;
            ForkActivation[] transitionBlocks = specProvider.TransitionBlocks;
            for (int i = 0; i < transitionBlocks.Length; i++)
            {
                bool useTimestamp = false;
                if (transitionBlocks[i].Timestamp != null)
                    useTimestamp = true;
                ulong transition = transitionBlocks[i].Timestamp ?? (ulong)transitionBlocks[i].BlockNumber;


                if ((useTimestamp && transition >= ImpossibleTimestampWithSpaceForImpossibleForks)
                    || (!useTimestamp && transition >= ImpossibleBlockNumberWithSpaceForImpossibleForks))
                {
                    continue;
                }

                if ((useTimestamp && transition > headTimestamp)
                    || (!useTimestamp && transition > (ulong)headNumber))
                {
                    next = transition;
                    break;
                }
            }

            return new ForkId(forkHash, next);
        }
    }
}
