// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Xdc.RLP;

public abstract class BaseXdcHeaderDecoder<TH> : RlpDecoder<BlockHeader>, IHeaderDecoder where TH : XdcBlockHeader
{
    private const int NonceLength = 8;

    // Encodes headers that aren't TH (e.g. plain BlockHeader test fixtures) using the base Ethereum
    // shape, mirroring AuRaHeaderDecoder's seal-only fallback. Needed once this decoder is registered
    // as the process-wide Rlp default (see XdcHeaderModule), so foreign headers still encode correctly
    // instead of throwing.
    private static readonly HeaderDecoder FallbackDecoder = new();

    protected static bool IsForSealing(RlpBehaviors beh)
        => (beh & RlpBehaviors.ForSealing) == RlpBehaviors.ForSealing;

    protected abstract TH CreateHeader(
        Hash256? parentHash,
        Hash256? unclesHash,
        Address? beneficiary,
        UInt256 difficulty,
        ulong number,
        ulong gasLimit,
        ulong timestamp,
        byte[]? extraData);

    protected abstract void DecodeHeaderSpecificFields(ref RlpReader decoderContext, TH header, RlpBehaviors rlpBehaviors, int headerCheck);
    protected abstract void EncodeHeaderSpecificFields<TWriter>(ref TWriter writer, TH header, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;
    protected abstract int GetHeaderSpecificContentLength(TH header, RlpBehaviors rlpBehaviors);

    protected override BlockHeader? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }

        ReadOnlySpan<byte> headerRlp = decoderContext.PeekNextItem();
        int headerSequenceLength = decoderContext.ReadSequenceLength();
        int headerCheck = decoderContext.Position + headerSequenceLength;

        // Common fields
        Hash256? parentHash = decoderContext.DecodeKeccak();
        Hash256? unclesHash = decoderContext.DecodeKeccak();
        Address? beneficiary = decoderContext.DecodeAddress();
        Hash256? stateRoot = decoderContext.DecodeKeccak();
        Hash256? transactionsRoot = decoderContext.DecodeKeccak();
        Hash256? receiptsRoot = decoderContext.DecodeKeccak();
        Bloom? bloom = decoderContext.DecodeBloom();
        UInt256 difficulty = decoderContext.DecodeUInt256();
        ulong number = decoderContext.DecodeULong();
        ulong gasLimit = decoderContext.DecodeULong();
        ulong gasUsed = decoderContext.DecodeULong();
        ulong timestamp = decoderContext.DecodeULong();
        byte[]? extraData = decoderContext.DecodeByteArray();

        TH header = CreateHeader(
            parentHash, unclesHash, beneficiary,
            difficulty, number, gasLimit, timestamp, extraData);

        header.StateRoot = stateRoot;
        header.TxRoot = transactionsRoot;
        header.ReceiptsRoot = receiptsRoot;
        header.Bloom = bloom;
        header.GasUsed = gasUsed;
        header.Hash = Keccak.Compute(headerRlp);

        header.MixHash = decoderContext.DecodeKeccak();
        header.Nonce = (ulong)decoderContext.DecodeUInt256(NonceLength);

        DecodeHeaderSpecificFields(ref decoderContext, header, rlpBehaviors, headerCheck);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(headerCheck);
        }

        return header;
    }

    public override void Encode<TWriter>(ref TWriter writer, BlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (header is null)
        {
            writer.EncodeNullObject();
            return;
        }

        if (header is not TH h)
        {
            FallbackDecoder.Encode(ref writer, header, rlpBehaviors);
            return;
        }

        writer.StartSequence(GetContentLength(h, rlpBehaviors));

        // Common fields
        writer.Encode(h.ParentHash);
        writer.Encode(h.UnclesHash);
        writer.Encode(h.Beneficiary);
        writer.Encode(h.StateRoot);
        writer.Encode(h.TxRoot);
        writer.Encode(h.ReceiptsRoot);
        writer.Encode(h.Bloom);
        writer.Encode(h.Difficulty);
        writer.Encode(h.Number);
        writer.Encode(h.GasLimit);
        writer.Encode(h.GasUsed);
        writer.Encode(h.Timestamp);
        writer.Encode(h.ExtraData);
        writer.Encode(h.MixHash);
        writer.Encode(h.Nonce, NonceLength);

        EncodeHeaderSpecificFields(ref writer, h, rlpBehaviors);
    }

    public override Rlp Encode(BlockHeader? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            return Rlp.OfEmptyList;
        }

        if (item is not TH header)
        {
            return FallbackDecoder.Encode(item, rlpBehaviors);
        }

        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        Encode(ref writer, item, rlpBehaviors);
        return new Rlp(bytes);
    }

    public override int GetLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
    {
        if (item is not TH header)
        {
            return FallbackDecoder.GetLength(item, rlpBehaviors);
        }

        return Rlp.LengthOfSequence(GetContentLength(header, rlpBehaviors));
    }

    private int GetContentLength(TH header, RlpBehaviors rlpBehaviors)
    {
        int contentLength =
            +Rlp.LengthOf(header.ParentHash)
            + Rlp.LengthOf(header.UnclesHash)
            + Rlp.LengthOf(header.Beneficiary)
            + Rlp.LengthOf(header.StateRoot)
            + Rlp.LengthOf(header.TxRoot)
            + Rlp.LengthOf(header.ReceiptsRoot)
            + Rlp.LengthOf(header.Bloom)
            + Rlp.LengthOf(header.Difficulty)
            + Rlp.LengthOf(header.Number)
            + Rlp.LengthOf(header.GasLimit)
            + Rlp.LengthOf(header.GasUsed)
            + Rlp.LengthOf(header.Timestamp)
            + Rlp.LengthOf(header.ExtraData)
            + Rlp.LengthOf(header.MixHash)
            + Rlp.LengthOfNonce(header.Nonce);

        contentLength += GetHeaderSpecificContentLength(header, rlpBehaviors);
        return contentLength;
    }

}
