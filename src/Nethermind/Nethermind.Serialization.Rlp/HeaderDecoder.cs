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
    public class HeaderDecoder() : RlpDecoder<BlockHeader>, IHeaderDecoder
    {
        public const int NonceLength = 8;

        protected override BlockHeader? DecodeInternal(ref RlpReader decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (RlpHelpers.TryConsumeNull(ref decoderContext, out ReadOnlySpan<byte> rlp, out int position)) return null;

            ReadOnlySpan<byte> headerRlp = rlp.Slice(position, RlpHelpers.PeekNextRlpLength(rlp, position));
            position = RlpHelpers.ReadSequenceLength(rlp, position, out int headerSequenceLength);
            int headerCheck = position + headerSequenceLength;

            position = RlpHelpers.DecodeKeccak(rlp, position, out Hash256 parentHash);
            position = RlpHelpers.DecodeKeccak(rlp, position, out Hash256 unclesHash);
            position = RlpHelpers.DecodeAddress(rlp, position, out Address beneficiary);
            position = RlpHelpers.DecodeKeccak(rlp, position, out Hash256 stateRoot);
            position = RlpHelpers.DecodeKeccak(rlp, position, out Hash256 transactionsRoot);
            position = RlpHelpers.DecodeKeccak(rlp, position, out Hash256 receiptsRoot);
            position = RlpHelpers.DecodeBloom(rlp, position, out Bloom bloom);
            position = RlpHelpers.DecodeUInt256(rlp, position, out UInt256 difficulty);
            position = RlpHelpers.DecodeULong(rlp, position, out ulong number);
            position = RlpHelpers.DecodeULong(rlp, position, out ulong gasLimit);
            position = RlpHelpers.DecodeULong(rlp, position, out ulong gasUsed);
            position = RlpHelpers.DecodeULong(rlp, position, out ulong timestamp);
            position = RlpHelpers.DecodeByteArray(rlp, position, out byte[] extraData);

            // The seal is a virtual extension point, so the cursor goes back to the reader once here.
            decoderContext.Position = position;
            BlockHeader blockHeader = DecodeSealAndCreateHeader(
                ref decoderContext, parentHash, unclesHash, beneficiary, in difficulty, number, gasLimit, timestamp, extraData);
            blockHeader.StateRoot = stateRoot;
            blockHeader.TxRoot = transactionsRoot;
            blockHeader.ReceiptsRoot = receiptsRoot;
            blockHeader.Bloom = bloom;
            blockHeader.GasUsed = gasUsed;
            blockHeader.Hash = Keccak.Compute(headerRlp);

            position = decoderContext.Position;

            if (position != headerCheck) position = RlpHelpers.DecodeUInt256(rlp, position, out blockHeader.BaseFeePerGas);
            if (position != headerCheck) (position, blockHeader.WithdrawalsRoot) = RlpHelpers.DecodeKeccak(rlp, position);
            if (position != headerCheck) (position, blockHeader.BlobGasUsed) = RlpHelpers.DecodeULong(rlp, position);
            if (position != headerCheck) (position, blockHeader.ExcessBlobGas) = RlpHelpers.DecodeULong(rlp, position);
            if (position != headerCheck) (position, blockHeader.ParentBeaconBlockRoot) = RlpHelpers.DecodeKeccakOrNull(rlp, position);
            if (position != headerCheck) (position, blockHeader.RequestsHash) = RlpHelpers.DecodeKeccakOrNull(rlp, position);
            if (position != headerCheck) (position, blockHeader.BlockAccessListHash) = RlpHelpers.DecodeKeccakOrNull(rlp, position);
            if (position != headerCheck) (position, blockHeader.SlotNumber) = RlpHelpers.DecodeULong(rlp, position);

            decoderContext.Position = position;

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                RlpHelpers.Check(position, headerCheck);
            }

            return blockHeader;
        }

        /// <summary>
        /// Decodes the seal section of the header and materialises the header instance.
        /// </summary>
        /// <remarks>
        /// The seal is the only header section whose shape depends on the consensus engine, and it also
        /// dictates the runtime header type, so decoding it and creating the instance is a single hook.
        /// The base implementation reads the Ethash/PoS <c>mixHash</c> + <c>nonce</c> pair; consensus
        /// plugins with a different seal shape override this (and <see cref="EncodeSeal"/> +
        /// <see cref="GetSealLength"/>) and register their decoder for <see cref="BlockHeader"/>.
        /// </remarks>
        protected virtual BlockHeader DecodeSealAndCreateHeader(
            ref RlpReader decoderContext,
            Hash256 parentHash,
            Hash256 unclesHash,
            Address beneficiary,
            in UInt256 difficulty,
            ulong number,
            ulong gasLimit,
            ulong timestamp,
            byte[] extraData)
        {
            Hash256 mixHash = decoderContext.DecodeKeccak();
            ulong nonce = (ulong)decoderContext.DecodeUInt256(NonceLength);
            return new BlockHeader(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData)
            {
                MixHash = mixHash,
                Nonce = nonce,
            };
        }

        /// <summary>Encodes the seal section. The base implementation writes <c>mixHash</c> + <c>nonce</c>.</summary>
        protected virtual void EncodeSeal<TWriter>(ref TWriter writer, BlockHeader header)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            writer.Encode(header.MixHash ?? Keccak.Zero);
            writer.Encode(header.Nonce, NonceLength);
        }

        /// <summary>RLP length of the seal section written by <see cref="EncodeSeal"/>.</summary>
        protected virtual int GetSealLength(BlockHeader header) =>
            Rlp.LengthOf(header.MixHash ?? Keccak.Zero) + Rlp.LengthOfNonce(header.Nonce);

        public override void Encode<TWriter>(ref TWriter writer, BlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (header is null)
            {
                writer.EncodeNullObject();
                return;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            writer.StartSequence(GetContentLength(header, rlpBehaviors));
            writer.Encode(header.ParentHash ?? Keccak.Zero);
            writer.Encode(header.UnclesHash ?? Keccak.OfAnEmptySequenceRlp);
            writer.Encode(header.Beneficiary ?? Address.Zero);
            writer.Encode(header.StateRoot ?? Keccak.EmptyTreeHash);
            writer.Encode(header.TxRoot ?? Keccak.EmptyTreeHash);
            writer.Encode(header.ReceiptsRoot ?? Keccak.EmptyTreeHash);
            writer.Encode(header.Bloom ?? Bloom.Empty);
            writer.Encode(header.Difficulty);
            writer.Encode(header.Number);
            writer.Encode(header.GasLimit);
            writer.Encode(header.GasUsed);
            writer.Encode(header.Timestamp);
            writer.Encode(header.ExtraData ?? []);

            if (notForSealing)
            {
                EncodeSeal(ref writer, header);
            }

            Span<bool> requiredItems = stackalloc bool[8];
            SetRequiredItems(header, requiredItems);

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

        private int GetContentLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return 0;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            int contentLength = 0
                                + Rlp.LengthOf(item.ParentHash ?? Keccak.Zero)
                                + Rlp.LengthOf(item.UnclesHash ?? Keccak.OfAnEmptySequenceRlp)
                                + Rlp.LengthOf(item.Beneficiary ?? Address.Zero)
                                + Rlp.LengthOf(item.StateRoot ?? Keccak.EmptyTreeHash)
                                + Rlp.LengthOf(item.TxRoot ?? Keccak.EmptyTreeHash)
                                + Rlp.LengthOf(item.ReceiptsRoot ?? Keccak.EmptyTreeHash)
                                + Rlp.LengthOf(item.Bloom ?? Bloom.Empty)
                                + Rlp.LengthOf(item.Difficulty)
                                + Rlp.LengthOf(item.Number)
                                + Rlp.LengthOf(item.GasLimit)
                                + Rlp.LengthOf(item.GasUsed)
                                + Rlp.LengthOf(item.Timestamp)
                                + Rlp.LengthOf(item.ExtraData ?? []);

            if (notForSealing)
            {
                contentLength += GetSealLength(item);
            }

            Span<bool> requiredItems = stackalloc bool[8];
            SetRequiredItems(item, requiredItems);

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

        private static void SetRequiredItems(BlockHeader header, Span<bool> requiredItems)
        {
            requiredItems[0] = !header.BaseFeePerGas.IsZero;
            requiredItems[1] = header.WithdrawalsRoot is not null;
            requiredItems[2] = header.BlobGasUsed is not null;
            // EIP-4844: BlobGasUsed and ExcessBlobGas are always encoded as a pair.
            requiredItems[3] = header.BlobGasUsed is not null || header.ExcessBlobGas is not null;
            requiredItems[4] = header.ParentBeaconBlockRoot is not null;
            requiredItems[5] = header.RequestsHash is not null;
            requiredItems[6] = header.BlockAccessListHash is not null;
            requiredItems[7] = header.SlotNumber is not null;

            for (int i = requiredItems.Length - 2; i >= 0; i--)
            {
                requiredItems[i] |= requiredItems[i + 1];
            }
        }

        public override int GetLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
            => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
}
