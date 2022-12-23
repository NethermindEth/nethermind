// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Force.Crc32;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Network
{
    public static class ForkInfo
    {
        private const long ImpossibleBlockNumberWithSpaceForImpossibleForks = long.MaxValue - 100;

        public static byte[] CalculateForkHash(ISpecProvider specProvider, long headNumber, Keccak genesisHash)
        {
            uint crc = 0;
            ForkActivation[] transitionBlocks = specProvider.TransitionBlocks;
            byte[] blockNumberBytes = new byte[8];
            crc = Crc32Algorithm.Append(crc, genesisHash.Bytes);
            for (int i = 0; i < transitionBlocks.Length; i++)
            {
                if (transitionBlocks[i] > headNumber)
                {
                    break;
                }

                BinaryPrimitives.WriteUInt64BigEndian(blockNumberBytes, (ulong)transitionBlocks[i].BlockNumber);
                crc = Crc32Algorithm.Append(crc, blockNumberBytes);
            }

            byte[] forkHash = new byte[4];
            BinaryPrimitives.TryWriteUInt32BigEndian(forkHash, crc);
            return forkHash;
        }

        public static ForkId CalculateForkId(ISpecProvider specProvider, long headNumber, Keccak genesisHash)
        {
            byte[] forkHash = CalculateForkHash(specProvider, headNumber, genesisHash);
            long next = 0;
            for (int i = 0; i < specProvider.TransitionBlocks.Length; i++)
            {
                if (specProvider.TransitionBlocks[i] >= ImpossibleBlockNumberWithSpaceForImpossibleForks)
                {
                    continue;
                }

                long transition = specProvider.TransitionBlocks[i].BlockNumber;
                if (transition > headNumber)
                {
                    next = transition;
                    break;
                }
            }

            return new ForkId(forkHash, next);
        }
    }
}
