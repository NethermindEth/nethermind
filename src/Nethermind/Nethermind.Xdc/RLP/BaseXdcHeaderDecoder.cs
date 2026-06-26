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
        Hash256 parentHash = decoderContext.DecodeKeccakNonNull();
        Hash256 unclesHash = decoderContext.DecodeKeccakNonNull();
        Address beneficiary = decoderContext.DecodeAddressNonNull();
        Hash256 stateRoot = decoderContext.DecodeKeccakNonNull();
        Hash256 transactionsRoot = decoderContext.DecodeKeccakNonNull();
        Hash256 receiptsRoot = decoderContext.DecodeKeccakNonNull();
        Bloom bloom = decoderContext.DecodeBloomNonNull();
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
            throw new ArgumentException($"Must be {typeof(TH).Name}.", nameof(header));

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
            throw new ArgumentException($"Must be {typeof(TH).Name}.", nameof(item));

        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        Encode(ref writer, item, rlpBehaviors);
        return new Rlp(bytes);
    }

    public override int GetLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
    {
        if (item is not TH header)
            throw new ArgumentException($"Must be {typeof(TH).Name}.", nameof(item));

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
