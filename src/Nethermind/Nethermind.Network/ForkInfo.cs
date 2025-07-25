// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Force.Crc32;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Synchronization;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Network
{
    public class ForkInfo(ISpecProvider specProvider, ISyncServer syncServer) : IForkInfo
    {
        private readonly Lock _initLock = new();
        private bool _wasInitialized = false;
        private Dictionary<uint, (ForkActivation Activation, ForkId Id)> DictForks { get; set; }
        internal (ForkActivation Activation, ForkId Id)[] Forks { get; set; }
        private bool _hasTimestampFork;

        internal void EnsureInitialized()
        {
            using var _ = _initLock.EnterScope();

            if (_wasInitialized) return;
            _wasInitialized = true;

            Hash256 genesisHash = syncServer.Genesis!.Hash;

            _hasTimestampFork = specProvider.TimestampFork != ISpecProvider.TimestampForkNever;
            ForkActivation[] transitionActivations = specProvider.TransitionActivations;
            DictForks = new();
            Forks = new (ForkActivation Activation, ForkId Id)[transitionActivations.Length + 1];
            byte[] blockNumberBytes = new byte[8];
            uint crc = Crc32Algorithm.Append(0, genesisHash.ThreadStaticBytes());
            // genesis fork activation
            SetFork(0, crc, ((0, null), new ForkId(crc, transitionActivations.Length > 0 ? transitionActivations[0].Activation : 0)));
            for (int index = 0; index < transitionActivations.Length; index++)
            {
                ForkActivation forkActivation = transitionActivations[index];
                BinaryPrimitives.WriteUInt64BigEndian(blockNumberBytes, forkActivation.Activation);
                crc = Crc32Algorithm.Append(crc, blockNumberBytes);
                SetFork(index + 1, crc, (forkActivation, new ForkId(crc, GetNextActivation(index, transitionActivations))));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetFork(int index, uint crc, (ForkActivation Activation, ForkId Id) fork)
        {
            Forks[index] = fork;
            DictForks.Add(crc, fork);
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
            EnsureInitialized();

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
            // Bit of a hack, if Next value is >= than genesis of oldest supported network it is timestamp.
            // Potentially we can parametrize it based on Spec provider, but not worth it for now
            // We support block forks up to 1,4 bln blocks
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IsTimestamp(ulong next) => next >= MainnetSpecProvider.GenesisBlockTimestamp;

            EnsureInitialized();

            if (head is null) return ValidationResult.Valid;
            if (!DictForks.TryGetValue(peerId.ForkHash, out (ForkActivation Activation, ForkId Id) found))
            {
                // Remote is on fork that does not exist for local. remote is incompatible or local is stale.
                return ValidationResult.IncompatibleOrStale;
            }

            bool forkIsLast = found.Id.Next == 0;
            bool peerForkIsLast = peerId.Next == 0;

            bool usingTimestamp = _hasTimestampFork
                                  && (forkIsLast || IsTimestamp(found.Id.Next)) &&
                                  (peerForkIsLast || IsTimestamp(peerId.Next));

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

        public ForkActivationsSummary GetForkActivationsSummary(BlockHeader? head)
        {
            ForkActivation headActivation = new(head?.Number ?? 0, head?.Number == 0 ? 0 : head?.Timestamp ?? 0);

            int indexOfActive = 0;
            for (; ; indexOfActive++)
            {
                ForkActivation fork = Forks[indexOfActive].Activation;

                if (indexOfActive >= Forks.Length - 1 ||
                    (fork.Timestamp.HasValue ? fork.Timestamp >= headActivation.Timestamp : fork.BlockNumber >= headActivation.BlockNumber))
                {
                    break;
                }
            }

            bool isNextPresent = indexOfActive < Forks.Length - 1;

            // The fix for post-merge genesis
            ForkActivation currentForkActivation = Forks[indexOfActive].Activation;

            if (currentForkActivation.BlockNumber is 0 && currentForkActivation.Timestamp is null)
            {
                currentForkActivation = new ForkActivation(0, 0);
            }

            return new ForkActivationsSummary
            {
                Current = currentForkActivation,
                CurrentForkId = Forks[indexOfActive].Id,

                Next = isNextPresent ? Forks[indexOfActive + 1].Activation : null,
                NextForkId = isNextPresent ? Forks[indexOfActive + 1].Id : null,

                Last = isNextPresent ? Forks[^1].Activation : null,
                LastForkId = isNextPresent ? Forks[^1].Id : null,
            };
        }
    }
}
