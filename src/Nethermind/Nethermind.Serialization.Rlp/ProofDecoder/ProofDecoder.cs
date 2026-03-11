// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BlockProofs;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.ProofDecoder
{
    public class ProofDecoder
    {
        public const int NonceLength = 8;

        public BlockHeaderProof? Decode(
            ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadSequenceLength();
                return null;
            }

            int contentLength = decoderContext.ReadSequenceLength();
            int checkPosition = decoderContext.Position + contentLength;

            BlockHeaderProofType proofType = (BlockHeaderProofType)decoderContext.DecodeByte();
            ReadOnlySpan<byte> payload = decoderContext.DecodeByteArraySpan();

            BlockHeaderProof result = DecodePayload(proofType, payload);
            decoderContext.Check(checkPosition);
            return result;
        }

        public BlockHeaderProof? Decode(
            RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ReadOnlySpan<byte> remaining = rlpStream.Data.AsSpan(rlpStream.Position, rlpStream.Length - rlpStream.Position);
            Rlp.ValueDecoderContext context = new(remaining);

            BlockHeaderProof? result = Decode(ref context, rlpBehaviors);
            rlpStream.Position += context.Position;

            return result;
        }

        public void Encode(
            RlpStream rlpStream,
            BlockHeaderProof? headerProof,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (headerProof is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            byte[] payload = EncodePayload(headerProof);

            int contentLength =
                Rlp.LengthOf((byte)headerProof.ProofType!) +
                Rlp.LengthOf(payload);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode((byte)headerProof.ProofType!);
            rlpStream.Encode(payload);
        }

        public Rlp Encode(BlockHeaderProof? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data.ToArray()!);
        }

        public int GetLength(BlockHeaderProof? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.LengthOfNull;
            }

            int payloadLength = GetPayloadLength(item);
            int contentLength =
                Rlp.LengthOf((byte)item.ProofType!) +
                Rlp.LengthOfByteString(payloadLength, 0);

            return Rlp.LengthOfSequence(contentLength);
        }

        private static int GetPayloadLength(BlockHeaderProof item)
        {
            return item.ProofType switch
            {
                BlockHeaderProofType.BlockProofHistoricalHashesAccumulator =>
                    SszEncoding.GetLength(
                        BlockProofHistoricalHashesAccumulator.From(item.HashesAccumulator!)),

                BlockHeaderProofType.BlockProofHistoricalRoots =>
                    SszEncoding.GetLength(
                        BlockProofHistoricalRoots.From(
                            item.BeaconBlockProof!,
                            item.BeaconBlockRoot!.Value,
                            item.ExecutionBlockProof!,
                            item.Slot!.Value)),

                BlockHeaderProofType.BlockProofHistoricalSummaries =>
                    SszEncoding.GetLength(
                        BlockProofHistoricalSummaries.From(
                            item.BeaconBlockProof!,
                            item.BeaconBlockRoot!.Value,
                            item.ExecutionBlockProof!,
                            item.Slot!.Value)),

                _ => throw new InvalidOperationException($"Invalid proof type: {item.ProofType}")
            };
        }

        private static byte[] EncodePayload(BlockHeaderProof headerProof)
        {
            return headerProof.ProofType switch
            {
                BlockHeaderProofType.BlockProofHistoricalHashesAccumulator =>
                    SszEncoding.Encode(
                        BlockProofHistoricalHashesAccumulator.From(headerProof.HashesAccumulator!)),

                BlockHeaderProofType.BlockProofHistoricalRoots =>
                    SszEncoding.Encode(
                        BlockProofHistoricalRoots.From(
                            headerProof.BeaconBlockProof!,
                            headerProof.BeaconBlockRoot!.Value,
                            headerProof.ExecutionBlockProof!,
                            headerProof.Slot!.Value)),

                BlockHeaderProofType.BlockProofHistoricalSummaries =>
                    SszEncoding.Encode(
                        BlockProofHistoricalSummaries.From(
                            headerProof.BeaconBlockProof!,
                            headerProof.BeaconBlockRoot!.Value,
                            headerProof.ExecutionBlockProof!,
                            headerProof.Slot!.Value)),

                _ => throw new InvalidOperationException($"Invalid proof type: {headerProof.ProofType}")
            };
        }

        private static BlockHeaderProof DecodePayload(
            BlockHeaderProofType proofType,
            ReadOnlySpan<byte> payload)
        {
            switch (proofType)
            {
                case BlockHeaderProofType.BlockProofHistoricalHashesAccumulator:
                {
                    SszEncoding.Decode(payload, out BlockProofHistoricalHashesAccumulator proof);
                    return new BlockHeaderProof(proof.HashesAccumulator);
                }

                case BlockHeaderProofType.BlockProofHistoricalRoots:
                {
                    SszEncoding.Decode(payload, out BlockProofHistoricalRoots proof);
                    return new BlockHeaderProof(
                        proof.BeaconBlockProof,
                        proof.ExecutionBlockProof,
                        proof.BeaconBlockRoot,
                        proof.Slot,
                        BlockHeaderProofType.BlockProofHistoricalRoots);
                }

                case BlockHeaderProofType.BlockProofHistoricalSummaries:
                {
                    SszEncoding.Decode(payload, out BlockProofHistoricalSummaries proof);
                    return new BlockHeaderProof(
                        proof.BeaconBlockProof,
                        proof.ExecutionBlockProof,
                        proof.BeaconBlockRoot,
                        proof.Slot);
                }

                default:
                    throw new InvalidOperationException($"Invalid proof type: {proofType}");
            }
        }
    }
}
