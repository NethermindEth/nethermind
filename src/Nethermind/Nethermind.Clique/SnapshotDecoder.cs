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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Clique
{
    internal class SnapshotDecoder : IRlpDecoder<Snapshot>
    {
        public Snapshot Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            context.ReadSequenceLength();
            
            // Config
            CliqueConfig config = new CliqueConfig(15, 30000);
            // Signature cache
            LruCache<Keccak, Address> sigCache = new LruCache<Keccak, Address>(CliqueSealEngine.InMemorySignatures);
            // Block number
            UInt256 number = context.DecodeUInt256();
            // Hash
            Keccak hash = context.DecodeKeccak();
            // Signers
            SortedList<Address, UInt256> signers = DecodeSigners(context);
            // Votes
            List<Vote> votes = DecodeVotes(context);
            // Tally
            Dictionary<Address, Tally> tally = DecodeTally(context);
            Snapshot snapshot = new Snapshot(config, sigCache, number, hash, signers, tally);
            snapshot.Votes = votes;

            return snapshot;
        }

        public Rlp Encode(Snapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Rlp.Encode(
                Rlp.Encode(item.Number),
                Rlp.Encode(item.Hash),
                Rlp.Encode(EncodeSigners(item.Signers)),
                Rlp.Encode(EncodeVotes(item.Votes)),
                Rlp.Encode(EncodeTally(item.Tally))
            );
        }

        private SortedList<Address, UInt256> DecodeSigners(Rlp.DecoderContext context)
        {
            context.ReadSequenceLength();
            SortedList<Address, UInt256> signers = new SortedList<Address, UInt256>(CliqueAddressComparer.Instance);
            int length = context.DecodeInt();
            for (int i = 0; i < length; i++)
            {
                Address signer = context.DecodeAddress();
                UInt256 signedAt = context.DecodeUInt256();
                signers.Add(signer, signedAt);
            }
            
            return signers;
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

        private Rlp[] EncodeSigners(SortedList<Address, UInt256> signers)
        {
            int signerCount = signers.Count;
            Rlp[] rlp = new Rlp[signerCount * 2 + 1];
            rlp[0] = Rlp.Encode(signerCount);
            int i = 0;
            foreach ((Address address, UInt256 signedAt) in signers)
            {
                rlp[i + 1] = Rlp.Encode(address);
                rlp[i + 2] = Rlp.Encode(signedAt);
                i += 2;
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