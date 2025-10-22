// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;


[SszSerializable]
public struct BlockProofHistoricalHashesAccumulator
{
    [SszVector(15)]
    public Root[] HashesAccumulator { get; set; }
}


[SszSerializable]
public struct BlockProofHistoricalRoots
{
    [SszVector(14)]
    public Root[] BeaconBlockProof { get; set; }
    
    public Root BeaconBlockRoot { get; set; }
    
    [SszVector(11)]
    public Root[] ExecutionBlockProof { get; set; }
    
    public long Slot { get; set; }
}

[SszSerializable]
public struct BlockProofHistoricalSummaries
{
    [SszVector(13)]
    public Root[] BeaconBlockProof { get; set; }
    
    public Root BeaconBlockRoot { get; set; }
    
    [SszList(12)]
    public Root[] ExecutionBlockProof { get; set; }
    
    public long Slot { get; set; }
}

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
                    BlockHeaderProof headerProof = new(
                        proof.HashesAccumulator,
                        BlockHeaderProofType.BlockProofHistoricalHashesAccumulator
                    );
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
                        proof.Slot,
                        BlockHeaderProofType.BlockProofHistoricalSummaries
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
                    BlockHeaderProof headerProof = new(
                        proof.HashesAccumulator,
                        BlockHeaderProofType.BlockProofHistoricalHashesAccumulator
                    );
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
                        proof.Slot,
                        BlockHeaderProofType.BlockProofHistoricalSummaries
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
            rlpStream.Write(SszEncoding.Encode(headerProof));
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
                headerProofLength = SszEncoding.GetLength(new BlockProofHistoricalHashesAccumulator { HashesAccumulator = item.HashesAccumulator! });
            }
            else if (item.ProofType == BlockHeaderProofType.BlockProofHistoricalRoots)
            {
                headerProofLength = SszEncoding.GetLength(new BlockProofHistoricalRoots { BeaconBlockProof = item.BeaconBlockProof!, ExecutionBlockProof = item.ExecutionBlockProof!, BeaconBlockRoot = item.BeaconBlockRoot!, Slot = item.Slot! });
            }
            else
            {
                headerProofLength = SszEncoding.GetLength(new BlockProofHistoricalSummaries { BeaconBlockProof = item.BeaconBlockProof!, ExecutionBlockProof = item.ExecutionBlockProof!, BeaconBlockRoot = item.BeaconBlockRoot!, Slot = item.Slot! });
            }


            return Rlp.LengthOf((byte)item.ProofType!) + Rlp.LengthOfByteString(headerProofLength, 0);
        }

        public int GetLength(BlockHeaderProof? item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }
    }
}
