// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Clique
{
    internal sealed class SnapshotDecoder : RlpDecoder<Snapshot>
    {
        protected override Snapshot DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpReader fields = decoderContext.EnterSequence(out int itemLength);

            // Block number
            ulong number = (ulong)fields.DecodeUInt256();
            // Hash
            Hash256 hash = fields.DecodeKeccak();
            // Signers
            SortedList<Address, ulong> signers = DecodeSigners(ref fields);
            // Votes
            List<Vote> votes = DecodeVotes(ref fields);
            // Tally
            Dictionary<Address, Tally> tally = DecodeTally(ref fields);
            Snapshot snapshot = new(number, hash, signers, tally) { Votes = votes };
            decoderContext.ExitSequenceIgnoringRest(fields, itemLength);

            return snapshot;
        }

        public override void Encode(ref RlpWriter writer, Snapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (int contentLength, int signersLength, int votesLength, int tallyLength) =
                GetContentLength(item, rlpBehaviors);
            RlpSequence sequence = writer.StartSequence(contentLength);
            writer.Encode(item.Number);
            writer.Encode(item.Hash);
            EncodeSigners(ref writer, item.Signers, signersLength);
            EncodeVotes(ref writer, item.Votes, votesLength);
            EncodeTally(ref writer, item.Tally, tallyLength);
            writer.EndSequence(sequence);

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

        private static SortedList<Address, ulong> DecodeSigners(ref RlpReader decoderContext)
        {
            RlpReader items = decoderContext.EnterSequence(out int itemLength);
            int length = items.DecodePositiveInt();
            Rlp.GuardLimit(length, items.Remaining);
            SortedList<Address, ulong> signers = new(GenericComparer.GetOptimized<Address>());
            for (int i = 0; i < length; i++)
            {
                Address signer = items.DecodeAddress();
                ulong signedAt = (ulong)items.DecodeUInt256();
                signers.Add(signer, signedAt);
            }

            decoderContext.ExitSequenceIgnoringRest(items, itemLength);
            return signers;
        }

        private static List<Vote> DecodeVotes(ref RlpReader decoderContext)
        {
            RlpReader items = decoderContext.EnterSequence(out int itemLength);
            int length = items.DecodePositiveInt();
            Rlp.GuardLimit(length, items.Remaining);
            List<Vote> votes = new(length);
            for (int i = 0; i < length; i++)
            {
                Address signer = items.DecodeAddress();
                ulong block = (ulong)items.DecodeUInt256();
                Address address = items.DecodeAddress();
                bool authorize = items.DecodeBool();
                Vote vote = new(signer, block, address, authorize);
                votes.Add(vote);
            }
            decoderContext.ExitSequenceIgnoringRest(items, itemLength);
            return votes;
        }

        private static Dictionary<Address, Tally> DecodeTally(ref RlpReader decoderContext)
        {
            RlpReader items = decoderContext.EnterSequence(out int itemLength);
            int length = items.DecodePositiveInt();
            Rlp.GuardLimit(length, items.Remaining);
            Dictionary<Address, Tally> tally = new(length);
            for (int i = 0; i < length; i++)
            {
                Address address = items.DecodeAddress();
                int votes = items.DecodeInt();
                bool authorize = items.DecodeBool();
                Tally tallyItem = new(authorize);
                tallyItem.Votes = votes;
                tally[address] = tallyItem;
            }
            decoderContext.ExitSequenceIgnoringRest(items, itemLength);
            return tally;
        }

        private static int GetSignersContentLength(SortedList<Address, ulong> signers)
        {
            int signerCount = signers.Count;
            int contentLength = Rlp.LengthOf(signerCount);
            int i = 0;
            foreach ((Address address, ulong signedAt) in signers)
            {
                contentLength += Rlp.LengthOf(address);
                contentLength += Rlp.LengthOf((UInt256)signedAt);
                i += 2;
            }
            return contentLength;
        }

        private static void EncodeSigners(ref RlpWriter writer, SortedList<Address, ulong> signers, int contentLength)
        {
            RlpSequence sequence = writer.StartSequence(contentLength);
            int signerCount = signers.Count;
            writer.Encode(signerCount);
            foreach ((Address address, ulong signedAt) in signers)
            {
                writer.Encode(address);
                writer.Encode(signedAt);
            }

            writer.EndSequence(sequence);
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

        private static void EncodeVotes(ref RlpWriter writer, List<Vote> votes, int contentLength)
        {
            RlpSequence sequence = writer.StartSequence(contentLength);
            int voteCount = votes.Count;
            writer.Encode(voteCount);
            for (int i = 0; i < voteCount; i++)
            {
                writer.Encode(votes[i].Signer);
                writer.Encode(votes[i].Block);
                writer.Encode(votes[i].Address);
                writer.Encode(votes[i].Authorize);
            }

            writer.EndSequence(sequence);
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

        private static void EncodeTally(ref RlpWriter writer, Dictionary<Address, Tally> tally, int contentLength)
        {
            RlpSequence sequence = writer.StartSequence(contentLength);
            int tallyCount = tally.Count;
            writer.Encode(tallyCount);
            foreach (KeyValuePair<Address, Tally> tallyItem in tally)
            {
                writer.Encode(tallyItem.Key);
                writer.Encode(tallyItem.Value.Votes);
                writer.Encode(tallyItem.Value.Authorize);
            }

            writer.EndSequence(sequence);
        }
    }
}
