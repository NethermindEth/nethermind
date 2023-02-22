// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Force.Crc32;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nethermind.Network
{
    public class ForkInfo
    {
        private Dictionary<uint, (ForkActivation Activation, ForkId Id)> DictForks { get; }
        private (ForkActivation Activation, ForkId Id)[] Forks { get; }
        private readonly ulong _timestampFork;

        public ForkInfo(ISpecProvider specProvider, Keccak genesisHash)
        {
            _timestampFork = specProvider.TimestampFork;
            ForkActivation[] transitionActivations = specProvider.TransitionActivations;
            DictForks = new();
            Forks = new (ForkActivation Activation, ForkId Id)[transitionActivations.Length + 1];
            byte[] blockNumberBytes = new byte[8];
            uint crc = 0;
            CalculateHash(ref crc, genesisHash.Bytes);
            // genesis fork activation
            (ForkActivation Activation, ForkId Id) toAdd = ((0, null), new ForkId(crc, transitionActivations.Length > 0 ? transitionActivations[0].Activation : 0));
            Forks[0] = toAdd;
            DictForks.Add(crc, toAdd);
            for (int index = 0; index < transitionActivations.Length; index++)
            {
                ForkActivation forkActivation = transitionActivations[index];
                BinaryPrimitives.WriteUInt64BigEndian(blockNumberBytes, forkActivation.Activation);
                CalculateHash(ref crc, blockNumberBytes);
                toAdd = (forkActivation, new ForkId(crc, GetNextActivation(index, transitionActivations)));
                Forks[index + 1] = toAdd;
                DictForks.Add(crc, toAdd);
            }
        }

        private static void CalculateHash(ref uint crc, byte[] bytes)
        {
            crc = Crc32Algorithm.Append(crc, bytes);
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

        /// <summary>
        /// Verify that the forkid from peer matches our forks.
        /// </summary>
        /// <param name="peerId"></param>
        /// <param name="head"></param>
        /// <returns></returns>
        public ValidationResult ValidateForkId(ForkId peerId, BlockHeader? head)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool InTimestampActivation(ulong next) => next >= _timestampFork;

            if (head == null) return ValidationResult.Valid;
            if (!DictForks.TryGetValue(peerId.ForkHash, out (ForkActivation Activation, ForkId Id) found))
            {
                // Remote is on fork that does not exist for local. remote is incompatible or local is stale.
                return ValidationResult.IncompatibleOrStale;
            }

            bool forkIsLast = found.Id.Next == 0;
            bool peerForkIsLast = peerId.Next == 0;

            bool usingTimestamp = (forkIsLast || InTimestampActivation(found.Id.Next))
                                  && (peerForkIsLast || InTimestampActivation(peerId.Next));

            ulong headActivation = usingTimestamp ? head.Timestamp : (ulong)head.Number;

            if (found.Id.Next != peerId.Next) // if the next fork is different
            {
                bool headPastLocalFork = headActivation >= found.Id.Next;
                if (peerForkIsLast && !forkIsLast && headPastLocalFork)
                {
                    // Remote does not know about a fork that local has already went through. remote is stale.
                    return ValidationResult.RemoteStale;
                }

                bool headPastPeerFork = headActivation >= peerId.Next;
                if (!peerForkIsLast && headPastPeerFork)
                {
                    // remote is expecting a fork that we passed but did not go through. remote is incompatible or local is stale.
                    return ValidationResult.IncompatibleOrStale;
                }
            }

            return ValidationResult.Valid;
        }
    }
}
