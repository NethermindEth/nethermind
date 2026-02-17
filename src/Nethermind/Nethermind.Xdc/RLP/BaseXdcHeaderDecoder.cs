// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Xdc;

public abstract class BaseXdcHeaderDecoder<TH> : IHeaderDecoder where TH : XdcBlockHeader
{
    private const int NonceLength = 8;

    protected static bool IsForSealing(RlpBehaviors beh)
        => (beh & RlpBehaviors.ForSealing) == RlpBehaviors.ForSealing;

    protected abstract TH CreateHeader(
        Hash256? parentHash,
        Hash256? unclesHash,
        Address? beneficiary,
        UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[]? extraData);

    protected abstract void DecodeHeaderSpecificFields(ref Rlp.ValueDecoderContext decoderContext, TH header, RlpBehaviors rlpBehaviors, int headerCheck);
    protected abstract void DecodeHeaderSpecificFields(RlpStream rlpStream, TH header, RlpBehaviors rlpBehaviors, int headerCheck);
    protected abstract void EncodeHeaderSpecificFields(RlpStream rlpStream, TH header, RlpBehaviors rlpBehaviors);
    protected abstract int GetHeaderSpecificContentLength(TH header, RlpBehaviors rlpBehaviors);

    public BlockHeader? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
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
        long number = decoderContext.DecodeLong();
        long gasLimit = decoderContext.DecodeLong();
        long gasUsed = decoderContext.DecodeLong();
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

    public BlockHeader? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        Span<byte> headerRlp = rlpStream.PeekNextItem();
        int headerSequenceLength = rlpStream.ReadSequenceLength();
        int headerCheck = rlpStream.Position + headerSequenceLength;

        // Common fields
        Hash256? parentHash = rlpStream.DecodeKeccak();
        Hash256? unclesHash = rlpStream.DecodeKeccak();
        Address? beneficiary = rlpStream.DecodeAddress();
        Hash256? stateRoot = rlpStream.DecodeKeccak();
        Hash256? transactionsRoot = rlpStream.DecodeKeccak();
        Hash256? receiptsRoot = rlpStream.DecodeKeccak();
        Bloom? bloom = rlpStream.DecodeBloom();
        UInt256 difficulty = rlpStream.DecodeUInt256();
        long number = rlpStream.DecodeLong();
        long gasLimit = rlpStream.DecodeLong();
        long gasUsed = rlpStream.DecodeLong();
        ulong timestamp = rlpStream.DecodeULong();
        byte[]? extraData = rlpStream.DecodeByteArray();

        TH header = CreateHeader(
            parentHash, unclesHash, beneficiary,
            difficulty, number, gasLimit, timestamp, extraData);

        header.StateRoot = stateRoot;
        header.TxRoot = transactionsRoot;
        header.ReceiptsRoot = receiptsRoot;
        header.Bloom = bloom;
        header.GasUsed = gasUsed;
        header.Hash = Keccak.Compute(headerRlp);

        header.MixHash = rlpStream.DecodeKeccak();
        header.Nonce = (ulong)rlpStream.DecodeUInt256(NonceLength);

        DecodeHeaderSpecificFields(rlpStream, header, rlpBehaviors, headerCheck);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(headerCheck);
        }

        return header;
    }

    public void Encode(RlpStream rlpStream, BlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (header is null)
        {
            rlpStream.EncodeNullObject();
            return;
        }

        if (header is not TH h)
            throw new ArgumentException($"Must be {typeof(TH).Name}.", nameof(header));

        rlpStream.StartSequence(GetContentLength(h, rlpBehaviors));

        // Common fields
        rlpStream.Encode(h.ParentHash);
        rlpStream.Encode(h.UnclesHash);
        rlpStream.Encode(h.Beneficiary);
        rlpStream.Encode(h.StateRoot);
        rlpStream.Encode(h.TxRoot);
        rlpStream.Encode(h.ReceiptsRoot);
        rlpStream.Encode(h.Bloom);
        rlpStream.Encode(h.Difficulty);
        rlpStream.Encode(h.Number);
        rlpStream.Encode(h.GasLimit);
        rlpStream.Encode(h.GasUsed);
        rlpStream.Encode(h.Timestamp);
        rlpStream.Encode(h.ExtraData);
        rlpStream.Encode(h.MixHash);
        rlpStream.Encode(h.Nonce, NonceLength);

        EncodeHeaderSpecificFields(rlpStream, h, rlpBehaviors);
    }

    public Rlp Encode(BlockHeader? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            return Rlp.OfEmptySequence;
        }

        if (item is not TH header)
            throw new ArgumentException($"Must be {typeof(TH).Name}.", nameof(item));

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public int GetLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
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
