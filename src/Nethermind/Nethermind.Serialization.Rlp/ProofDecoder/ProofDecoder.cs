// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp
{
    // public interface IHeaderDecoder : IBlockHeaderDecoder<BlockHeader> { }
    // public interface IBlockHeaderDecoder<T> : IRlpValueDecoder<T>, IRlpStreamDecoder<T> where T : BlockHeader { }

    public class ProofDecoder
    {
        public const int NonceLength = 8;

        public BlockHeaderProof? Decode(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                return null;
            }

            var proofType = (BlockHeaderProofType) decoderContext.ReadByte();

            switch (proofType) {
                case BlockHeaderProofType.BlockProofHistoricalHashesAccumulator: {
                    // ssz decode
                    SszEncoding.Decode(decoderContext.DecodeByteArraySpan(), out BlockProofHistoricalHashesAccumulator proof);
                    BlockHeaderProof headerProof = new(proof.HashesAccumulator);
                    return headerProof;
                }
                case BlockHeaderProofType.BlockProofHistoricalRoots: {
                    // ssz decode container
                    SszEncoding.Decode(decoderContext.DecodeByteArraySpan(), out BlockProofHistoricalRoots proof);
                    BlockHeaderProof headerProof = new(
                        proof.BeaconBlockProof,
                        proof.ExecutionBlockProof,
                        proof.BeaconBlockRoot,
                        proof.Slot,
                        BlockHeaderProofType.BlockProofHistoricalRoots
                    );
                    return headerProof;
                }
                case BlockHeaderProofType.BlockProofHistoricalSummaries: {
                    // ssz decode container
                    SszEncoding.Decode(decoderContext.DecodeByteArraySpan(), out BlockProofHistoricalSummaries proof);
                    BlockHeaderProof headerProof = new(
                        proof.BeaconBlockProof,
                        proof.ExecutionBlockProof,
                        proof.BeaconBlockRoot,
                        proof.Slot
                    );
                    return headerProof;
                }
                default: {
                    throw new InvalidOperationException($"Invalid proof type: {proofType}");
                }
            }
        }

        public BlockHeaderProof? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            var proofType = (BlockHeaderProofType) rlpStream.ReadByte();

            switch (proofType) {
                case BlockHeaderProofType.BlockProofHistoricalHashesAccumulator: {
                    // ssz decode
                    SszEncoding.Decode(rlpStream.DecodeByteArraySpan(), out BlockProofHistoricalHashesAccumulator proof);
                    BlockHeaderProof headerProof = new(proof.HashesAccumulator);
                    return headerProof;
                }
                case BlockHeaderProofType.BlockProofHistoricalRoots: {
                    // ssz decode container
                    SszEncoding.Decode(rlpStream.DecodeByteArraySpan(), out BlockProofHistoricalRoots proof);
                    BlockHeaderProof headerProof = new(
                        proof.BeaconBlockProof,
                        proof.ExecutionBlockProof,
                        proof.BeaconBlockRoot,
                        proof.Slot,
                        BlockHeaderProofType.BlockProofHistoricalRoots
                    );
                    return headerProof;
                }
                case BlockHeaderProofType.BlockProofHistoricalSummaries: {
                    // ssz decode container
                    SszEncoding.Decode(rlpStream.DecodeByteArraySpan(), out BlockProofHistoricalSummaries proof);
                    BlockHeaderProof headerProof = new(
                        proof.BeaconBlockProof,
                        proof.ExecutionBlockProof,
                        proof.BeaconBlockRoot,
                        proof.Slot
                    );
                    return headerProof;
                }
                default: {
                    throw new InvalidOperationException($"Invalid proof type: {proofType}");
                }
            }
        }

        public void Encode(RlpStream rlpStream, BlockHeaderProof? headerProof, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (headerProof is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            rlpStream.WriteByte((byte)headerProof.ProofType!);
            rlpStream.StartByteArray(GetContentLength(headerProof, rlpBehaviors), false);
            switch (headerProof.ProofType) {
                case BlockHeaderProofType.BlockProofHistoricalHashesAccumulator: {
                    rlpStream.Write(SszEncoding.Encode(BlockProofHistoricalHashesAccumulator.From(headerProof.HashesAccumulator!)));
                    break;
                }
                case BlockHeaderProofType.BlockProofHistoricalRoots: {
                    rlpStream.Write(
                        SszEncoding.Encode(
                            BlockProofHistoricalRoots.From(
                                headerProof.BeaconBlockProof!, 
                                headerProof.BeaconBlockRoot!.Value, 
                                headerProof.ExecutionBlockProof!, 
                                headerProof.Slot!.Value
                            )
                        )
                    );
                    break;
                }
                case BlockHeaderProofType.BlockProofHistoricalSummaries: {
                    rlpStream.Write(
                        SszEncoding.Encode(
                            BlockProofHistoricalSummaries.From(
                                headerProof.BeaconBlockProof!, 
                                headerProof.BeaconBlockRoot!.Value, 
                                headerProof.ExecutionBlockProof!, 
                                headerProof.Slot!.Value
                            )
                        )
                    );
                    break;
                }
            }
        }

        public Rlp Encode(BlockHeaderProof? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);

            return new Rlp(rlpStream.Data.ToArray());
        }

        private static int GetContentLength(BlockHeaderProof? item, RlpBehaviors _)
        {
            if (item is null)
            {
                return 0;
            }

            int headerProofLength;

            if (item.ProofType == BlockHeaderProofType.BlockProofHistoricalHashesAccumulator)
            {
                headerProofLength = SszEncoding.GetLength(BlockProofHistoricalHashesAccumulator.From(item.HashesAccumulator!));
            }
            else if (item.ProofType == BlockHeaderProofType.BlockProofHistoricalRoots)
            {
                headerProofLength = SszEncoding.GetLength(
                    BlockProofHistoricalRoots.From(
                        item.BeaconBlockProof!, 
                        item.BeaconBlockRoot!.Value, 
                        item.ExecutionBlockProof!, 
                        item.Slot!.Value
                    )
                );
            }
            else
            {
                headerProofLength = SszEncoding.GetLength(
                    BlockProofHistoricalSummaries.From(
                        item.BeaconBlockProof!, 
                        item.BeaconBlockRoot!.Value, 
                        item.ExecutionBlockProof!, 
                        item.Slot!.Value
                    )
                );
            }


            return Rlp.LengthOf((byte)item.ProofType!) + Rlp.LengthOfByteString(headerProofLength, 0);
        }

        public int GetLength(BlockHeaderProof? item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }
    }
}
