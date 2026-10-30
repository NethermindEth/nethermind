// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    public interface IHeaderDecoder : IBlockHeaderDecoder<BlockHeader> { }
    public interface IBlockHeaderDecoder<T> : IRlpDecoder<T> where T : BlockHeader { }

    /// <remarks>
    /// Without a spec provider a zero-length logs bloom is rejected; with one it is accepted only where EIP-7668 is
    /// active, which needs the header's number and timestamp, so that check follows them.
    /// </remarks>
    public class HeaderDecoder : RlpDecoder<BlockHeader>, IHeaderDecoder
    {
        public const int NonceLength = 8;

        private readonly ISpecProvider? _specProvider;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HeaderDecoder))]
        public HeaderDecoder() { }

        /// <param name="specProvider">Resolves EIP-7668 activation, which allows a zero-length logs bloom.</param>
        public HeaderDecoder(ISpecProvider? specProvider) => _specProvider = specProvider;

        protected override BlockHeader? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

        private BlockHeader? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence()) return null;

            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
            RlpReader item = reader;
            ReadOnlySpan<byte> headerRlp = item.ReadRaw(itemLength);

            Hash256 parentHash = fields.DecodeKeccak();
            Hash256 unclesHash = fields.DecodeKeccak();
            Address beneficiary = fields.DecodeAddress();
            Hash256 stateRoot = fields.DecodeKeccak();
            Hash256 transactionsRoot = fields.DecodeKeccak();
            Hash256 receiptsRoot = fields.DecodeKeccak();
            Bloom bloom = _specProvider is null ? fields.DecodeBloom() : fields.DecodeBloomOrZeroLength();
            UInt256 difficulty = fields.DecodeUInt256<TCanonicalChecks>();
            ulong number = fields.DecodeULong<TCanonicalChecks>();
            ulong gasLimit = fields.DecodeULong<TCanonicalChecks>();
            ulong gasUsed = fields.DecodeULong<TCanonicalChecks>();
            ulong timestamp = fields.DecodeULong<TCanonicalChecks>();
            if (bloom.IsZeroLength && !_specProvider!.GetSpec(new ForkActivation(number, timestamp)).IsEip7668Enabled)
            {
                // The same exception DecodeBloom throws for a zero-length bloom.
                Rlp.GuardSize(actual: 0, expected: Bloom.ByteLength);
            }

            byte[] extraData = fields.DecodeByteArray<TCanonicalChecks>();

            BlockHeader blockHeader = DecodeSealAndCreateHeader(
                ref fields, parentHash, unclesHash, beneficiary, in difficulty, number, gasLimit, timestamp, extraData);
            blockHeader.StateRoot = stateRoot;
            blockHeader.TxRoot = transactionsRoot;
            blockHeader.ReceiptsRoot = receiptsRoot;
            blockHeader.Bloom = bloom;
            blockHeader.GasUsed = gasUsed;
            blockHeader.Hash = Keccak.Compute(headerRlp);

            if (fields.HasNext) blockHeader.BaseFeePerGas = fields.DecodeUInt256<TCanonicalChecks>();
            if (fields.HasNext) blockHeader.WithdrawalsRoot = fields.DecodeKeccak();
            if (fields.HasNext) blockHeader.BlobGasUsed = fields.DecodeULong<TCanonicalChecks>();
            if (fields.HasNext) blockHeader.ExcessBlobGas = fields.DecodeULong<TCanonicalChecks>();
            if (fields.HasNext) blockHeader.ParentBeaconBlockRoot = fields.DecodeKeccakOrNull();
            if (fields.HasNext) blockHeader.RequestsHash = fields.DecodeKeccakOrNull();
            if (fields.HasNext) blockHeader.BlockAccessListHash = fields.DecodeKeccakOrNull();
            if (fields.HasNext) blockHeader.SlotNumber = fields.DecodeULong<TCanonicalChecks>();

            reader.ExitSequence(in fields, itemLength, rlpBehaviors);
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
            ulong nonce = BinaryPrimitives.ReadUInt64BigEndian(decoderContext.ReadBytes(NonceLength));
            return new BlockHeader(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData)
            {
                MixHash = mixHash,
                Nonce = nonce,
            };
        }

        /// <summary>Encodes the seal section. The base implementation writes <c>mixHash</c> + <c>nonce</c>.</summary>
        protected virtual void EncodeSeal(ref RlpWriter writer, BlockHeader header)
        {
            writer.Encode(header.MixHash ?? Keccak.Zero);
            Span<byte> nonce = stackalloc byte[NonceLength];
            BinaryPrimitives.WriteUInt64BigEndian(nonce, header.Nonce);
            writer.Write(nonce);
        }

        /// <summary>RLP length of the seal section written by <see cref="EncodeSeal"/>.</summary>
        protected virtual int GetSealLength(BlockHeader header) =>
            Rlp.LengthOf(header.MixHash ?? Keccak.Zero) + Rlp.LengthOfNonce(header.Nonce);

        public override void Encode(ref RlpWriter writer, BlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (header is null)
            {
                writer.EncodeNullObject();
                return;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            int optionalFields = GetOptionalFieldCount(header);
            RlpSequence sequence = writer.StartSequence(GetContentLength(header, notForSealing, optionalFields));
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

            if (optionalFields > 0) writer.Encode(header.BaseFeePerGas);
            if (optionalFields > 1) writer.Encode(header.WithdrawalsRoot ?? Keccak.Zero);
            if (optionalFields > 2) writer.Encode(header.BlobGasUsed.GetValueOrDefault());
            if (optionalFields > 3) writer.Encode(header.ExcessBlobGas.GetValueOrDefault());
            if (optionalFields > 4) writer.Encode(header.ParentBeaconBlockRoot);
            if (optionalFields > 5) writer.Encode(header.RequestsHash);
            if (optionalFields > 6) writer.Encode(header.BlockAccessListHash);
            if (optionalFields > 7) writer.Encode(header.SlotNumber.GetValueOrDefault());
            writer.EndSequence(sequence);
        }

        private int GetContentLength(BlockHeader item, bool notForSealing, int optionalFields)
        {
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

            if (optionalFields > 0) contentLength += Rlp.LengthOf(item.BaseFeePerGas);
            if (optionalFields > 1) contentLength += Rlp.LengthOf(item.WithdrawalsRoot ?? Keccak.Zero);
            if (optionalFields > 2) contentLength += Rlp.LengthOf(item.BlobGasUsed.GetValueOrDefault());
            if (optionalFields > 3) contentLength += Rlp.LengthOf(item.ExcessBlobGas.GetValueOrDefault());
            if (optionalFields > 4) contentLength += Rlp.LengthOf(item.ParentBeaconBlockRoot);
            if (optionalFields > 5) contentLength += Rlp.LengthOf(item.RequestsHash);
            if (optionalFields > 6) contentLength += Rlp.LengthOf(item.BlockAccessListHash);
            if (optionalFields > 7) contentLength += Rlp.LengthOf(item.SlotNumber.GetValueOrDefault());

            return contentLength;
        }

        /// <summary>Gets the number of fields after the seal to encode.</summary>
        /// <remarks>
        /// The fields are positional, so a field is encoded when it or a later field is set, and an unset field before
        /// a set one takes its default value. EIP-4844: BlobGasUsed and ExcessBlobGas are always encoded as a pair.
        /// </remarks>
        private static int GetOptionalFieldCount(BlockHeader header) =>
            header.SlotNumber is not null ? 8
            : header.BlockAccessListHash is not null ? 7
            : header.RequestsHash is not null ? 6
            : header.ParentBeaconBlockRoot is not null ? 5
            : header.BlobGasUsed is not null || header.ExcessBlobGas is not null ? 4
            : header.WithdrawalsRoot is not null ? 2
            : !header.BaseFeePerGas.IsZero ? 1
            : 0;

        public override int GetLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
            => item is null
                ? Rlp.OfEmptyList.Length
                : Rlp.LengthOfSequence(GetContentLength(item, (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing, GetOptionalFieldCount(item)));
    }
}
