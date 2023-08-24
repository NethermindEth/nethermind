// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Clique
{
    internal class SnapshotDecoder : IRlpStreamDecoder<Snapshot>
    {
        public Snapshot Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();

            // Block number
            long number = (long)rlpStream.DecodeUInt256();
            // Hash
            Keccak hash = rlpStream.DecodeKeccak();
            // Signers
            SortedList<Address, long> signers = DecodeSigners(rlpStream);
            // Votes
            List<Vote> votes = DecodeVotes(rlpStream);
            // Tally
            Dictionary<Address, Tally> tally = DecodeTally(rlpStream);
            Snapshot snapshot = new Snapshot(number, hash, signers, tally);
            snapshot.Votes = votes;

            return snapshot;
        }

        public void Encode(RlpStream stream, Snapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (int contentLength, int signersLength, int votesLength, int tallyLength) =
                GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            stream.Encode((UInt256)item.Number);
            stream.Encode(item.Hash);
            EncodeSigners(stream, item.Signers, signersLength);
            EncodeVotes(stream, item.Votes, votesLength);
            EncodeTally(stream, item.Tally, tallyLength);

        }

        public int GetLength(Snapshot item, RlpBehaviors rlpBehaviors)
        {
            (int contentLength, int _, int _, int _) = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(contentLength);
        }

        private (int contentLength, int signersLength, int votesLength, int tallyLength) GetContentLength(Snapshot item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int signersLength = GetSignersContentLength(item.Signers);
            int votesLength = GetVotesContentLength(item.Votes);
            int tallyLength = GetTallyContentLength(item.Tally);

            int contentLength = Rlp.LengthOf((UInt256)item.Number);
            contentLength += Rlp.LengthOf(item.Hash);
            contentLength += Rlp.LengthOfSequence(signersLength);
            contentLength += Rlp.LengthOfSequence(votesLength);
            contentLength += Rlp.LengthOfSequence(tallyLength);

            return (contentLength, signersLength, votesLength, tallyLength);
        }

        private SortedList<Address, long> DecodeSigners(RlpStream rlpStream)
        {
            rlpStream.ReadSequenceLength();
            SortedList<Address, long> signers = new SortedList<Address, long>(AddressComparer.Instance);
            int length = rlpStream.DecodeInt();
            for (int i = 0; i < length; i++)
            {
                Address signer = rlpStream.DecodeAddress();
                long signedAt = (long)rlpStream.DecodeUInt256();
                signers.Add(signer, signedAt);
            }

            return signers;
        }

        private List<Vote> DecodeVotes(RlpStream rlpStream)
        {
            rlpStream.ReadSequenceLength();
            List<Vote> votes = new List<Vote>();
            int length = rlpStream.DecodeInt();
            for (int i = 0; i < length; i++)
            {
                Address signer = rlpStream.DecodeAddress();
                long block = (long)rlpStream.DecodeUInt256();
                Address address = rlpStream.DecodeAddress();
                bool authorize = rlpStream.DecodeBool();
                Vote vote = new Vote(signer, block, address, authorize);
                votes.Add(vote);
            }
            return votes;
        }

        private Dictionary<Address, Tally> DecodeTally(RlpStream rlpStream)
        {
            rlpStream.ReadSequenceLength();
            Dictionary<Address, Tally> tally = new Dictionary<Address, Tally>();
            int length = rlpStream.DecodeInt();
            for (int i = 0; i < length; i++)
            {
                Address address = rlpStream.DecodeAddress();
                int votes = rlpStream.DecodeInt();
                bool authorize = rlpStream.DecodeBool();
                Tally tallyItem = new Tally(authorize);
                tallyItem.Votes = votes;
                tally[address] = tallyItem;
            }
            return tally;
        }

        private int GetSignersContentLength(SortedList<Address, long> signers)
        {
            int signerCount = signers.Count;
            int contentLength = Rlp.LengthOf(signerCount);
            int i = 0;
            foreach ((Address address, long signedAt) in signers)
            {
                contentLength += Rlp.LengthOf(address);
                contentLength += Rlp.LengthOf((UInt256)signedAt);
                i += 2;
            }
            return contentLength;
        }

        private void EncodeSigners(RlpStream stream, SortedList<Address, long> signers, int contentLength)
        {
            stream.StartSequence(contentLength);
            int signerCount = signers.Count;
            stream.Encode(signerCount);
            int i = 0;
            foreach ((Address address, long signedAt) in signers)
            {
                stream.Encode(address);
                stream.Encode((UInt256)signedAt);
                i += 2;
            }
        }

        private int GetVotesContentLength(List<Vote> votes)
        {
            int voteCount = votes.Count;
            int contentLength = Rlp.LengthOf(voteCount);
            for (int i = 0; i < voteCount; i++)
            {
                contentLength += Rlp.LengthOf(votes[i].Signer);
                contentLength += Rlp.LengthOf((UInt256)votes[i].Block);
                contentLength += Rlp.LengthOf(votes[i].Address);
                contentLength += Rlp.LengthOf(votes[i].Authorize);
            }

            return contentLength;
        }

        private void EncodeVotes(RlpStream stream, List<Vote> votes, int contentLength)
        {
            stream.StartSequence(contentLength);
            int voteCount = votes.Count;
            stream.Encode(voteCount);
            Rlp[] rlp = new Rlp[4 * voteCount + 1];
            rlp[0] = Rlp.Encode(voteCount);
            for (int i = 0; i < voteCount; i++)
            {
                stream.Encode(votes[i].Signer);
                stream.Encode((UInt256)votes[i].Block);
                stream.Encode(votes[i].Address);
                stream.Encode(votes[i].Authorize);
            }
        }

        private int GetTallyContentLength(Dictionary<Address, Tally> tally)
        {
            int tallyCount = tally.Count;
            int contentLength = Rlp.LengthOf(tallyCount);
            int i = 0;
            foreach (KeyValuePair<Address, Tally> tallyItem in tally)
            {
                contentLength += Rlp.LengthOf(tallyItem.Key);
                contentLength += Rlp.LengthOf(tallyItem.Value.Votes);
                contentLength += Rlp.LengthOf(tallyItem.Value.Authorize);
                i++;
            }
            return contentLength;
        }

        private void EncodeTally(RlpStream stream, Dictionary<Address, Tally> tally, int contentLength)
        {
            stream.StartSequence(contentLength);
            int tallyCount = tally.Count;
            stream.Encode(tallyCount);
            Rlp[] rlp = new Rlp[3 * tallyCount + 1];
            rlp[0] = Rlp.Encode(tallyCount);
            int i = 0;
            foreach (KeyValuePair<Address, Tally> tallyItem in tally)
            {
                stream.Encode(tallyItem.Key);
                stream.Encode(tallyItem.Value.Votes);
                stream.Encode(tallyItem.Value.Authorize);
                i++;
            }
        }
    }
}
