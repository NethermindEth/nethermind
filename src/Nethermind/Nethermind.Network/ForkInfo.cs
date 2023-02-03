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
        private ulong TimstampThreshold => 1438269973ul; // MainnetSpecProvider.ShanghaiBlockTimestamp;

        public ForkInfo(ISpecProvider specProvider, Keccak genesisHash)
        {
            ForkActivation[] transitionActivations = specProvider.TransitionActivations;
            DictForks = new();
            Forks = new (ForkActivation Activation, ForkId Id)[transitionActivations.Length + 1];
            byte[] blockNumberBytes = new byte[8];
            uint crc = 0;
            byte[] hash = CalculateHash(ref crc, genesisHash.Bytes);
            // genesis fork activation
            (ForkActivation Activation, ForkId Id) toAdd = ((0, null), new ForkId(hash, transitionActivations.Length > 0 ? transitionActivations[0].Activation : 0));
            Forks[0] = toAdd;
            DictForks.Add(BitConverter.ToUInt32(hash), toAdd);
            for (int index = 0; index < transitionActivations.Length; index++)
            {
                ForkActivation forkActivation = transitionActivations[index];
                BinaryPrimitives.WriteUInt64BigEndian(blockNumberBytes, forkActivation.Activation);
                hash = CalculateHash(ref crc, blockNumberBytes);
                toAdd = (forkActivation, new ForkId(hash, GetNextActivation(index, transitionActivations)));
                Forks[index + 1] = toAdd;
                DictForks.Add(BitConverter.ToUInt32(hash), toAdd);
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

        /// <summary>
        /// Verify that the forkid from peer matches our forks. Code is largely copied from Geth.
        /// </summary>
        /// <param name="peerId"></param>
        /// <returns></returns>
        public ValidationResult ValidateForkId(ForkId peerId, BlockHeader? head)
        {
            // Run the fork checksum validation ruleset:
            //   1. If local and remote FORK_CSUM matches, compare local head to FORK_NEXT.
            //        The two nodes are in the same fork state currently. They might know
            //        of differing future forks, but that's not relevant until the fork
            //        triggers (might be postponed, nodes might be updated to match).
            //      1a. A remotely announced but remotely not passed block is already passed
            //          locally, disconnect, since the chains are incompatible.
            //      1b. No remotely announced fork; or not yet passed locally, connect.
            //   2. If the remote FORK_CSUM is a subset of the local past forks and the
            //      remote FORK_NEXT matches with the locally following fork block number,
            //      connect.
            //        Remote node is currently syncing. It might eventually diverge from
            //        us, but at this current point in time we don't have enough information.
            //   3. If the remote FORK_CSUM is a superset of the local past forks and can
            //      be completed with locally known future forks, connect.
            //        Local node is currently syncing. It might eventually diverge from
            //        the remote, but at this current point in time we don't have enough
            //        information.
            //   4. Reject in all other cases.
            if (head == null) return ValidationResult.Valid;

            for (int i = 0; i < Forks.Length; i++)
            {
                (ForkActivation forkActivation, ForkId forkId) = Forks[i];
                bool usingTimestamp = (forkId.Next == 0
                || forkId.Next >= TimstampThreshold)
                && (peerId.Next == 0
                || peerId.Next >= TimstampThreshold);
                ulong headActivation = (usingTimestamp ? head.Timestamp : (ulong)head.Number);

                // If our head is beyond this fork, continue to the next (we have a dummy
                // fork of maxuint64 as the last item to always fail this check eventually).
                if (i + 1 < Forks.Length && headActivation >= Forks[i + 1].Activation.Activation) continue;

                // Found the first unpassed fork block, check if our current state matches
                // the remote checksum (rule #1).
                if (Bytes.AreEqual(forkId.ForkHash, peerId.ForkHash))
                {
                    // Fork checksum matched, check if a remote future fork block already passed
                    // locally without the local node being aware of it (rule #1a).
                    if (peerId.Next > 0 && headActivation >= peerId.Next)
                    {
                        return ValidationResult.IncompatibleOrStale;
                    }
                    // Haven't passed locally a remote-only fork, accept the connection (rule #1b).
                    return ValidationResult.Valid;
                }

                // The local and remote nodes are in different forks currently, check if the
                // remote checksum is a subset of our local forks (rule #2).
                for (int j = 0; j < i; j++)
                {
                    if (Bytes.AreEqual(Forks[j].Id.ForkHash, peerId.ForkHash))
                    {
                        // Remote checksum is a subset, validate based on the announced next fork
                        if (Forks[j + 1].Activation.Activation != peerId.Next)
                        {
                            return ValidationResult.RemoteStale;
                        }

                        return ValidationResult.Valid;
                    }
                }

                // Remote chain is not a subset of our local one, check if it's a superset by
                // any chance, signalling that we're simply out of sync (rule #3).
                for (int j = i + 1; j < Forks.Length; j++)
                {
                    if (Bytes.AreEqual(Forks[j].Id.ForkHash, peerId.ForkHash))
                    {
                        // Yay, remote checksum is a superset, ignore upcoming forks
                        return ValidationResult.Valid;
                    }
                }
                // No exact, subset or superset match. We are on differing chains, reject.
                return ValidationResult.IncompatibleOrStale;
            }

            throw new InvalidOperationException();
        }

        /// <summary>
        /// Verify that the forkid from peer matches our forks.
        /// </summary>
        /// <param name="peerId"></param>
        /// <returns></returns>
        public ValidationResult ValidateForkId2(ForkId peerId, BlockHeader? head)
        {
            if (head == null) return ValidationResult.Valid;
            if (!DictForks.TryGetValue(BitConverter.ToUInt32(peerId.ForkHash), out (ForkActivation Activation, ForkId Id) found))
            {
                return ValidationResult.IncompatibleOrStale;
            }
            bool usingTimestamp = (found.Id.Next == 0
                || found.Id.Next >= TimstampThreshold)
                && (peerId.Next == 0
                || peerId.Next >= TimstampThreshold);
            ulong headActivation = (usingTimestamp ? head.Timestamp : (ulong)head.Number);

            // my approach is to accept all peers except the ones we dont like. which is the oposite of what
            // geth does. they reject all except ones they think are right.

            if (found.Id.Next != peerId.Next)
            {
                if (peerId.Next == 0
                    && found.Id.Next > 0
                    && headActivation >= found.Id.Next)
                {
                    return ValidationResult.RemoteStale;
                }
                if (peerId.Next > 0
                    && headActivation > found.Activation.Activation)
                {
                    if (headActivation >= peerId.Next)
                    {
                        return ValidationResult.IncompatibleOrStale;
                    }
                    if (found.Id.Next > 0
                        && headActivation >= found.Id.Next
                        && peerId.Next != found.Id.Next)
                    {
                        return ValidationResult.IncompatibleOrStale;
                    }
                }
            }
            return ValidationResult.Valid;
        }
    }
}
