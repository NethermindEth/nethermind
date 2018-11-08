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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Clique
{
    internal class SnapshotDecoder : IRlpDecoder<Snapshot>
    {
        public Snapshot Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            context.ReadSequenceLength();
            
            // Config
            CliqueConfig config = new CliqueConfig();
            // Signature cache
            LruCache<Keccak, Address> sigCache = new LruCache<Keccak, Address>(Clique.InmemorySignatures);
            // Block number
            UInt256 number = context.DecodeUInt256();
            // Hash
            Keccak hash = context.DecodeKeccak();
            // Signers
            HashSet<Address> signers = DecodeSigners(context);
            // Recent
            Dictionary<ulong, Address> recent = DecodeRecent(context);
            // Votes
            List<Vote> votes = DecodeVotes(context);
            // Tally
            Dictionary<Address, Tally> tally = DecodeTally(context);
            Snapshot snapshot = new Snapshot(config, sigCache, number, hash, signers, recent, tally);
            snapshot.Votes = votes;

            return snapshot;
        }

        public Rlp Encode(Snapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Rlp.Encode(
                Rlp.Encode(item.Number),
                Rlp.Encode(item.Hash),
                Rlp.Encode(EncodeSigners(item.Signers)),
                Rlp.Encode(EncodeRecent(item.Recent)),
                Rlp.Encode(EncodeVotes(item.Votes)),
                Rlp.Encode(EncodeTally(item.Tally))
            );
        }

        private HashSet<Address> DecodeSigners(Rlp.DecoderContext context)
        {
            context.ReadSequenceLength();
            HashSet<Address> signers = new HashSet<Address>();
            int length = context.DecodeInt();
            for (int i = 0; i < length; i++)
            {
                Address signer = context.DecodeAddress();
                signers.Add(signer);
            }
            return signers;
        }

        private Dictionary<ulong, Address> DecodeRecent(Rlp.DecoderContext context)
        {
            context.ReadSequenceLength();
            Dictionary<ulong, Address> recent = new Dictionary<ulong, Address>();
            int length = context.DecodeInt();
            for (int i = 0; i < length; i++)
            {
                ulong number = (ulong)context.DecodeUInt256();
                Address signer = context.DecodeAddress();
                recent[number] = signer;
            }
            return recent;
        }

        private List<Vote> DecodeVotes(Rlp.DecoderContext context)
        {
            context.ReadSequenceLength();
            List<Vote> votes = new List<Vote>();
            int length = context.DecodeInt();
            for (int i = 0; i < length; i++)
            {
                Address signer = context.DecodeAddress();
                UInt256 block = context.DecodeUInt256();
                Address address = context.DecodeAddress();
                bool authorize = context.DecodeBool();
                Vote vote = new Vote(signer, block, address, authorize);
                votes.Add(vote);
            }
            return votes;
        }

        private Dictionary<Address, Tally> DecodeTally(Rlp.DecoderContext context)
        {
            context.ReadSequenceLength();
            Dictionary<Address, Tally> tally = new Dictionary<Address, Tally>();
            int length = context.DecodeInt();
            for (int i = 0; i < length; i++)
            {
                Address address = context.DecodeAddress();
                int votes = context.DecodeInt();
                bool authorize = context.DecodeBool();
                Tally tallyItem = new Tally(authorize);
                tallyItem.Votes = votes;
                tally[address] = tallyItem;
            }
            return tally;
        }

        private Rlp[] EncodeSigners(HashSet<Address> signers)
        {
            int signerCount = signers.Count;
            Rlp[] rlp = new Rlp[signerCount + 1];
            rlp[0] = Rlp.Encode(signerCount);
            int i = 0;
            foreach (Address signer in signers)
            {
                rlp[i + 1] = Rlp.Encode(signer);
                i++;
            }
            return rlp;
        }

        private Rlp[] EncodeRecent(Dictionary<ulong, Address> recent)
        {
            int recentCount = recent.Count;
            Rlp[] rlp = new Rlp[2 * recentCount + 1];
            rlp[0] = Rlp.Encode(recentCount);
            int i = 0;
            foreach (var signing in recent)
            {
                rlp[2 * i + 1] = Rlp.Encode(new UInt256(signing.Key));
                rlp[2 * i + 2] = Rlp.Encode(signing.Value);
                i++;
            }
            for (int j = 0; j < rlp.Count(); j++)
            {
            }
            return rlp;
        }

        private Rlp[] EncodeVotes(List<Vote> votes)
        {
            int voteCount = votes.Count;
            Rlp[] rlp = new Rlp[4 * voteCount + 1];
            rlp[0] = Rlp.Encode(voteCount);
            for (int i = 0; i < voteCount; i++)
            {
                rlp[4 * i + 1] = Rlp.Encode(votes[i].Signer);
                rlp[4 * i + 2] = Rlp.Encode(votes[i].Block);
                rlp[4 * i + 3] = Rlp.Encode(votes[i].Address);
                rlp[4 * i + 4] = Rlp.Encode(votes[i].Authorize);
            }
            return rlp;
        }

        private Rlp[] EncodeTally(Dictionary<Address, Tally> tally)
        {
            int tallyCount = tally.Count;
            Rlp[] rlp = new Rlp[3 * tallyCount + 1];
            rlp[0] = Rlp.Encode(tallyCount);
            int i = 0;
            foreach (var tallyItem in tally)
            {
                rlp[3 * i + 1] = Rlp.Encode(tallyItem.Key);
                rlp[3 * i + 2] = Rlp.Encode(tallyItem.Value.Votes);
                rlp[3 * i + 3] = Rlp.Encode(tallyItem.Value.Authorize);
                i++;
            }
            return rlp;
        }
    }
}