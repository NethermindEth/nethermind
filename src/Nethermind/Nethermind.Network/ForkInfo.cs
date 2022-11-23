// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            ForkActivation[] transitionBlocks = specProvider.TransitionActivations;
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
            ForkActivation[] transitionBlocks = specProvider.TransitionActivations;
            for (int i = 0; i < transitionBlocks.Length; i++)
            {
                ulong transition = transitionBlocks[i].Timestamp ?? (ulong)transitionBlocks[i].BlockNumber;
                bool useTimestamp = transitionBlocks[i].Timestamp is not null;

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
