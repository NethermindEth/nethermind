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

        protected override BlockHeader? DecodeInternal(ref Rlp.ValueDecoderContext decoderContext,
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

            BlockHeader blockHeader = CreateHeader(parentHash, unclesHash, beneficiary, in difficulty, number, gasLimit, timestamp, extraData!);
            blockHeader.StateRoot = stateRoot;
            blockHeader.TxRoot = transactionsRoot;
            blockHeader.ReceiptsRoot = receiptsRoot;
            blockHeader.Bloom = bloom;
            blockHeader.GasUsed = gasUsed;
            blockHeader.Hash = Keccak.Compute(headerRlp);

            DecodeSeal(ref decoderContext, blockHeader);

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

        /// <summary>
        /// Construct the typed <see cref="BlockHeader"/> for decoding. Subclasses (e.g. AuRa)
        /// override to produce their subclass; <see cref="DecodeSeal"/> then fills in the
        /// engine-specific seal fields.
        /// </summary>
        protected virtual BlockHeader CreateHeader(
            Hash256? parentHash, Hash256? unclesHash, Address? beneficiary,
            in UInt256 difficulty, long number, long gasLimit, ulong timestamp, byte[] extraData)
            => new(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);

        /// <summary>
        /// Decode the seal section (mixHash + nonce for Ethereum, step + signature for AuRa) onto
        /// <paramref name="header"/>. Default reads the Ethash shape; subclasses override.
        /// </summary>
        protected virtual void DecodeSeal(ref Rlp.ValueDecoderContext decoderContext, BlockHeader header)
        {
            header.MixHash = decoderContext.DecodeKeccak();
            header.Nonce = (ulong)decoderContext.DecodeUInt256(NonceLength);
        }

        public override void Encode(RlpStream rlpStream, BlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (header is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            rlpStream.StartSequence(GetContentLength(header, rlpBehaviors));
            rlpStream.Encode(header.ParentHash);
            rlpStream.Encode(header.UnclesHash);
            rlpStream.Encode(header.Beneficiary);
            rlpStream.Encode(header.StateRoot);
            rlpStream.Encode(header.TxRoot);
            rlpStream.Encode(header.ReceiptsRoot);
            rlpStream.Encode(header.Bloom);
            rlpStream.Encode(header.Difficulty);
            rlpStream.Encode(header.Number);
            rlpStream.Encode(header.GasLimit);
            rlpStream.Encode(header.GasUsed);
            rlpStream.Encode(header.Timestamp);
            rlpStream.Encode(header.ExtraData);

            if (notForSealing)
            {
                EncodeSeal(rlpStream, header);
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

            if (requiredItems[0]) rlpStream.Encode(header.BaseFeePerGas);
            if (requiredItems[1]) rlpStream.Encode(header.WithdrawalsRoot ?? Keccak.Zero);
            if (requiredItems[2]) rlpStream.Encode(header.BlobGasUsed.GetValueOrDefault());
            if (requiredItems[3]) rlpStream.Encode(header.ExcessBlobGas.GetValueOrDefault());
            if (requiredItems[4]) rlpStream.Encode(header.ParentBeaconBlockRoot);
            if (requiredItems[5]) rlpStream.Encode(header.RequestsHash);
            if (requiredItems[6]) rlpStream.Encode(header.BlockAccessListHash);
            if (requiredItems[7]) rlpStream.Encode(header.SlotNumber.GetValueOrDefault());
        }

        /// <summary>
        /// Encode the seal section. Default writes mixHash + nonce; subclasses (e.g. AuRa) override.
        /// </summary>
        protected virtual void EncodeSeal(RlpStream rlpStream, BlockHeader header)
        {
            rlpStream.Encode(header.MixHash);
            rlpStream.Encode(header.Nonce, NonceLength);
        }

        public override Rlp Encode(BlockHeader? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);

            return new Rlp(rlpStream.Data.ToArray());
        }

        protected int GetContentLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
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
                contentLength += GetSealContentLength(item);
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

        /// <summary>
        /// RLP byte cost of the seal section. Default returns the Ethash mixHash + nonce cost.
        /// </summary>
        protected virtual int GetSealContentLength(BlockHeader header)
            => Rlp.LengthOf(header.MixHash) + Rlp.LengthOfNonce(header.Nonce);

        public override int GetLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
            => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
}
