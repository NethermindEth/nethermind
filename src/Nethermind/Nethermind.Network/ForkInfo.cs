// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Force.Crc32;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nethermind.Network
{
    public class ForkInfo
    {
        private (ForkActivation Activation, ForkId Id)[] Forks { get; }

        public ForkInfo(ISpecProvider specProvider, Keccak genesisHash)
        {
            ForkActivation[] transitionActivations = specProvider.TransitionActivations;
            Forks = new (ForkActivation Activation, ForkId Id)[transitionActivations.Length + 1];
            byte[] blockNumberBytes = new byte[8];
            uint crc = 0;
            byte[] hash = CalculateHash(ref crc, genesisHash.Bytes);
            // genesis fork activation
            Forks[0] = ((0, null), new ForkId(hash, transitionActivations.Length > 0 ? transitionActivations[0].Activation : 0));
            for (int index = 0; index < transitionActivations.Length; index++)
            {
                ForkActivation forkActivation = transitionActivations[index];
                BinaryPrimitives.WriteUInt64BigEndian(blockNumberBytes, forkActivation.Activation);
                hash = CalculateHash(ref crc, blockNumberBytes);
                Forks[index + 1] = (forkActivation, new ForkId(hash, GetNextActivation(index, transitionActivations)));
            }
        }

        private static byte[] CalculateHash(ref uint crc, byte[] bytes)
        {
            crc = Crc32Algorithm.Append(crc, bytes);
            byte[] forkHash = new byte[4];
            BinaryPrimitives.TryWriteUInt32BigEndian(forkHash, crc);
            return forkHash;
        }

        private static ulong GetNextActivation(int index, ForkActivation[] transitionActivations)
        {
            static T? GetActivationPrimitive<T>(T? activation, T delta) where T : struct, INumber<T>, IMinMaxValue<T> =>
                activation is null ? default : activation < T.MaxValue - delta ? activation : T.Zero;

            static ulong GetActivation(ForkActivation forkActivation) =>
                GetActivationPrimitive(forkActivation.Timestamp, 4UL)
                ?? (ulong)GetActivationPrimitive(forkActivation.BlockNumber, 4L);

            index += 1;
            return index < transitionActivations.Length
                ? GetActivation(transitionActivations[index])
                : 0;
        }

        public ForkId GetForkId(long headNumber, ulong headTimestamp)
        {
            return Forks.TryGetSearchedItem(
                new ForkActivation(headNumber, headTimestamp),
                CompareTransitionOnActivation, out (ForkActivation Activation, ForkId Id) fork)
                ? fork.Id
                : throw new InvalidOperationException("Fork not found");
        }

        private static int CompareTransitionOnActivation(ForkActivation activation, (ForkActivation Activation, ForkId _) transition) =>
            activation.CompareTo(transition.Activation);
    }
}
