// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Clique
{
    internal sealed class SnapshotDecoder : RlpDecoder<Snapshot>
    {
        protected override Snapshot DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            decoderContext.ReadSequenceLength();

            // Block number
            long number = (long)decoderContext.DecodeUInt256();
            // Hash
            Hash256 hash = decoderContext.DecodeKeccak();
            // Signers
            SortedList<Address, long> signers = DecodeSigners(ref decoderContext);
            // Votes
            List<Vote> votes = DecodeVotes(ref decoderContext);
            // Tally
            Dictionary<Address, Tally> tally = DecodeTally(ref decoderContext);
            Snapshot snapshot = new(number, hash, signers, tally) { Votes = votes };

            return snapshot;
        }

        public override void Encode<TWriter>(ref TWriter writer, Snapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (int contentLength, int signersLength, int votesLength, int tallyLength) =
                GetContentLength(item, rlpBehaviors);
            writer.StartSequence(contentLength);
            writer.Encode(item.Number);
            writer.Encode(item.Hash);
            EncodeSigners(ref writer, item.Signers, signersLength);
            EncodeVotes(ref writer, item.Votes, votesLength);
            EncodeTally(ref writer, item.Tally, tallyLength);

        }

        public override int GetLength(Snapshot item, RlpBehaviors rlpBehaviors)
        {
            (int contentLength, int _, int _, int _) = GetContentLength(item, rlpBehaviors);
            return Rlp.LengthOfSequence(contentLength);
        }

        private static (int contentLength, int signersLength, int votesLength, int tallyLength) GetContentLength(Snapshot item,
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

        private static SortedList<Address, long> DecodeSigners(ref RlpReader decoderContext)
        {
            decoderContext.ReadSequenceLength();
            int length = decoderContext.DecodePositiveInt();
            decoderContext.GuardLimit(length);
            SortedList<Address, long> signers = new(GenericComparer.GetOptimized<Address>());
            for (int i = 0; i < length; i++)
            {
                Address signer = decoderContext.DecodeAddress();
                long signedAt = (long)decoderContext.DecodeUInt256();
                signers.Add(signer, signedAt);
            }

            return signers;
        }

        private static List<Vote> DecodeVotes(ref RlpReader decoderContext)
        {
            decoderContext.ReadSequenceLength();
            int length = decoderContext.DecodePositiveInt();
            decoderContext.GuardLimit(length);
            List<Vote> votes = new(length);
            for (int i = 0; i < length; i++)
            {
                Address signer = decoderContext.DecodeAddress();
                long block = (long)decoderContext.DecodeUInt256();
                Address address = decoderContext.DecodeAddress();
                bool authorize = decoderContext.DecodeBool();
                Vote vote = new(signer, block, address, authorize);
                votes.Add(vote);
            }
            return votes;
        }

        private static Dictionary<Address, Tally> DecodeTally(ref RlpReader decoderContext)
        {
            decoderContext.ReadSequenceLength();
            int length = decoderContext.DecodePositiveInt();
            decoderContext.GuardLimit(length);
            Dictionary<Address, Tally> tally = new(length);
            for (int i = 0; i < length; i++)
            {
                Address address = decoderContext.DecodeAddress();
                int votes = decoderContext.DecodeInt();
                bool authorize = decoderContext.DecodeBool();
                Tally tallyItem = new(authorize);
                tallyItem.Votes = votes;
                tally[address] = tallyItem;
            }
            return tally;
        }

        private static int GetSignersContentLength(SortedList<Address, long> signers)
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

        private static void EncodeSigners<TWriter>(ref TWriter writer, SortedList<Address, long> signers, int contentLength)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            writer.StartSequence(contentLength);
            int signerCount = signers.Count;
            writer.Encode(signerCount);
            foreach ((Address address, long signedAt) in signers)
            {
                writer.Encode(address);
                writer.Encode(signedAt);
            }
        }

        private static int GetVotesContentLength(List<Vote> votes)
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

        private static void EncodeVotes<TWriter>(ref TWriter writer, List<Vote> votes, int contentLength)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            writer.StartSequence(contentLength);
            int voteCount = votes.Count;
            writer.Encode(voteCount);
            for (int i = 0; i < voteCount; i++)
            {
                writer.Encode(votes[i].Signer);
                writer.Encode(votes[i].Block);
                writer.Encode(votes[i].Address);
                writer.Encode(votes[i].Authorize);
            }
        }

        private static int GetTallyContentLength(Dictionary<Address, Tally> tally)
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

        private static void EncodeTally<TWriter>(ref TWriter writer, Dictionary<Address, Tally> tally, int contentLength)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            writer.StartSequence(contentLength);
            int tallyCount = tally.Count;
            writer.Encode(tallyCount);
            foreach (KeyValuePair<Address, Tally> tallyItem in tally)
            {
                writer.Encode(tallyItem.Key);
                writer.Encode(tallyItem.Value.Votes);
                writer.Encode(tallyItem.Value.Authorize);
            }
        }
    }
}
