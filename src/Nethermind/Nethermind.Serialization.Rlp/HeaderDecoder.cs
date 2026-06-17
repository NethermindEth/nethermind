// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    public interface IHeaderDecoder : IBlockHeaderDecoder<BlockHeader> { }
    public interface IBlockHeaderDecoder<T> : IRlpDecoder<T> where T : BlockHeader { }

    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HeaderDecoder))]
    public sealed class HeaderDecoder() : RlpDecoder<BlockHeader>, IHeaderDecoder
    {
        public const int NonceLength = 8;

        protected override BlockHeader? DecodeInternal(ref RlpReader decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            ReadOnlySpan<byte> headerRlp = decoderContext.PeekNextItem();
            int headerSequenceLength = decoderContext.ReadSequenceLength();
            int headerCheck = decoderContext.Position + headerSequenceLength;

            Hash256? parentHash = decoderContext.DecodeKeccak();
            Hash256? unclesHash = decoderContext.DecodeKeccak();
            Address? beneficiary = decoderContext.DecodeAddress();
            Hash256? stateRoot = decoderContext.DecodeKeccak();
            Hash256? transactionsRoot = decoderContext.DecodeKeccak();
            Hash256? receiptsRoot = decoderContext.DecodeKeccak();
            Bloom? bloom = decoderContext.DecodeBloom();
            UInt256 difficulty = decoderContext.DecodeUInt256();
            long number = decoderContext.DecodeLong();
            long gasLimit = decoderContext.DecodeLong();
            long gasUsed = decoderContext.DecodeLong();
            ulong timestamp = decoderContext.DecodeULong();
            byte[]? extraData = decoderContext.DecodeByteArray();

            BlockHeader blockHeader = new(
                parentHash,
                unclesHash,
                beneficiary,
                difficulty,
                number,
                gasLimit,
                timestamp,
                extraData)
            {
                StateRoot = stateRoot,
                TxRoot = transactionsRoot,
                ReceiptsRoot = receiptsRoot,
                Bloom = bloom,
                GasUsed = gasUsed,
                Hash = Keccak.Compute(headerRlp)
            };

            if (decoderContext.PeekPrefixAndContentLength().ContentLength == Hash256.Size)
            {
                blockHeader.MixHash = decoderContext.DecodeKeccak();
                blockHeader.Nonce = (ulong)decoderContext.DecodeUInt256(NonceLength);
            }
            else
            {
                blockHeader.AuRaStep = (long)decoderContext.DecodeUInt256();
                blockHeader.AuRaSignature = decoderContext.DecodeByteArray();
            }

            if (decoderContext.Position != headerCheck) blockHeader.BaseFeePerGas = decoderContext.DecodeUInt256();
            if (decoderContext.Position != headerCheck) blockHeader.WithdrawalsRoot = decoderContext.DecodeKeccak();
            if (decoderContext.Position != headerCheck) blockHeader.BlobGasUsed = decoderContext.DecodeULong();
            if (decoderContext.Position != headerCheck) blockHeader.ExcessBlobGas = decoderContext.DecodeULong();
            if (decoderContext.Position != headerCheck) blockHeader.ParentBeaconBlockRoot = decoderContext.DecodeKeccak();
            if (decoderContext.Position != headerCheck) blockHeader.RequestsHash = decoderContext.DecodeKeccak();
            if (decoderContext.Position != headerCheck) blockHeader.BlockAccessListHash = decoderContext.DecodeKeccak();
            if (decoderContext.Position != headerCheck) blockHeader.SlotNumber = decoderContext.DecodeULong();

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(headerCheck);
            }

            return blockHeader;
        }

        public override void Encode<TWriter>(ref TWriter writer, BlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (header is null)
            {
                writer.EncodeNullObject();
                return;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            writer.StartSequence(GetContentLength(header, rlpBehaviors));
            writer.Encode(header.ParentHash);
            writer.Encode(header.UnclesHash);
            writer.Encode(header.Beneficiary);
            writer.Encode(header.StateRoot);
            writer.Encode(header.TxRoot);
            writer.Encode(header.ReceiptsRoot);
            writer.Encode(header.Bloom);
            writer.Encode(header.Difficulty);
            writer.Encode(header.Number);
            writer.Encode(header.GasLimit);
            writer.Encode(header.GasUsed);
            writer.Encode(header.Timestamp);
            writer.Encode(header.ExtraData);

            if (notForSealing)
            {
                bool isAuRa = header.AuRaSignature is not null;
                if (isAuRa)
                {
                    writer.Encode(header.AuRaStep!.Value);
                    writer.Encode(header.AuRaSignature);
                }
                else
                {
                    writer.Encode(header.MixHash);
                    writer.Encode(header.Nonce, NonceLength);
                }
            }

            Span<bool> requiredItems = stackalloc bool[8];
            requiredItems[0] = !header.BaseFeePerGas.IsZero;
            requiredItems[1] = header.WithdrawalsRoot is not null;
            requiredItems[2] = header.BlobGasUsed is not null;
            requiredItems[3] = header.BlobGasUsed is not null || header.ExcessBlobGas is not null; // EIP-4844: BlobGasUsed, ExcessBlobGas always encoded as a pair
            requiredItems[4] = header.ParentBeaconBlockRoot is not null;
            requiredItems[5] = header.RequestsHash is not null;
            requiredItems[6] = header.BlockAccessListHash is not null;
            requiredItems[7] = header.SlotNumber is not null;

            for (int i = 6; i >= 0; i--)
            {
                requiredItems[i] |= requiredItems[i + 1];
            }

            if (requiredItems[0]) writer.Encode(header.BaseFeePerGas);
            if (requiredItems[1]) writer.Encode(header.WithdrawalsRoot ?? Keccak.Zero);
            if (requiredItems[2]) writer.Encode(header.BlobGasUsed.GetValueOrDefault());
            if (requiredItems[3]) writer.Encode(header.ExcessBlobGas.GetValueOrDefault());
            if (requiredItems[4]) writer.Encode(header.ParentBeaconBlockRoot);
            if (requiredItems[5]) writer.Encode(header.RequestsHash);
            if (requiredItems[6]) writer.Encode(header.BlockAccessListHash);
            if (requiredItems[7]) writer.Encode(header.SlotNumber.GetValueOrDefault());
        }

        public override Rlp Encode(BlockHeader? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList;
            }

            byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
            RlpWriter writer = new(bytes);
            Encode(ref writer, item, rlpBehaviors);

            return new Rlp(bytes);
        }

        private static int GetContentLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return 0;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            int contentLength = 0
                                + Rlp.LengthOf(item.ParentHash)
                                + Rlp.LengthOf(item.UnclesHash)
                                + Rlp.LengthOf(item.Beneficiary)
                                + Rlp.LengthOf(item.StateRoot)
                                + Rlp.LengthOf(item.TxRoot)
                                + Rlp.LengthOf(item.ReceiptsRoot)
                                + Rlp.LengthOf(item.Bloom)
                                + Rlp.LengthOf(item.Difficulty)
                                + Rlp.LengthOf(item.Number)
                                + Rlp.LengthOf(item.GasLimit)
                                + Rlp.LengthOf(item.GasUsed)
                                + Rlp.LengthOf(item.Timestamp)
                                + Rlp.LengthOf(item.ExtraData);

            if (notForSealing)
            {
                bool isAuRa = item.AuRaSignature is not null;
                if (isAuRa)
                {
                    contentLength += Rlp.LengthOf(item.AuRaStep!.Value);
                    contentLength += Rlp.LengthOf(item.AuRaSignature);
                }
                else
                {
                    contentLength += Rlp.LengthOf(item.MixHash);
                    contentLength += Rlp.LengthOfNonce(item.Nonce);
                }
            }


            Span<bool> requiredItems = stackalloc bool[8];
            requiredItems[0] = !item.BaseFeePerGas.IsZero;
            requiredItems[1] = item.WithdrawalsRoot is not null;
            requiredItems[2] = item.BlobGasUsed is not null;
            requiredItems[3] = item.BlobGasUsed is not null || item.ExcessBlobGas is not null; // EIP-4844: BlobGasUsed, ExcessBlobGas always encoded as a pair
            requiredItems[4] = item.ParentBeaconBlockRoot is not null;
            requiredItems[5] = item.RequestsHash is not null;
            requiredItems[6] = item.BlockAccessListHash is not null;
            requiredItems[7] = item.SlotNumber is not null;

            for (int i = 6; i >= 0; i--)
            {
                requiredItems[i] |= requiredItems[i + 1];
            }

            if (requiredItems[0]) contentLength += Rlp.LengthOf(item.BaseFeePerGas);
            if (requiredItems[1]) contentLength += Rlp.LengthOf(item.WithdrawalsRoot ?? Keccak.Zero);
            if (requiredItems[2]) contentLength += Rlp.LengthOf(item.BlobGasUsed.GetValueOrDefault());
            if (requiredItems[3]) contentLength += Rlp.LengthOf(item.ExcessBlobGas.GetValueOrDefault());
            if (requiredItems[4]) contentLength += Rlp.LengthOf(item.ParentBeaconBlockRoot);
            if (requiredItems[5]) contentLength += Rlp.LengthOf(item.RequestsHash);
            if (requiredItems[6]) contentLength += Rlp.LengthOf(item.BlockAccessListHash);
            if (requiredItems[7]) contentLength += Rlp.LengthOf(item.SlotNumber.GetValueOrDefault());

            return contentLength;
        }

        public override int GetLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
            => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
}
