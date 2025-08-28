// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
namespace Nethermind.Xdc;
public class XdcHeaderDecoder : IRlpValueDecoder<XdcBlockHeader>, IRlpStreamDecoder<XdcBlockHeader>
{
    private const int NonceLength = 8;

    public XdcBlockHeader? Decode(ref Rlp.ValueDecoderContext decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
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

        XdcBlockHeader blockHeader = new(
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

        blockHeader.MixHash = decoderContext.DecodeKeccak();
        blockHeader.Nonce = (ulong)decoderContext.DecodeUInt256(NonceLength);

        blockHeader.Validators = decoderContext.DecodeByteArray();
        blockHeader.Validator = decoderContext.DecodeByteArray();
        blockHeader.Penalties = decoderContext.DecodeByteArray();

        if (decoderContext.Position != headerCheck) blockHeader.BaseFeePerGas = decoderContext.DecodeUInt256();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(headerCheck);
        }

        return blockHeader;
    }

    public XdcBlockHeader? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        Span<byte> headerRlp = rlpStream.PeekNextItem();
        int headerSequenceLength = rlpStream.ReadSequenceLength();
        int headerCheck = rlpStream.Position + headerSequenceLength;

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

        XdcBlockHeader blockHeader = new(
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

        blockHeader.MixHash = rlpStream.DecodeKeccak();
        blockHeader.Nonce = (ulong)rlpStream.DecodeUInt256(NonceLength);

        blockHeader.Validators = rlpStream.DecodeByteArray();
        blockHeader.Validator = rlpStream.DecodeByteArray();
        blockHeader.Penalties = rlpStream.DecodeByteArray();

        if (rlpStream.Position != headerCheck) blockHeader.BaseFeePerGas = rlpStream.DecodeUInt256();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(headerCheck);
        }

        return blockHeader;
    }

    public void Encode(RlpStream rlpStream, XdcBlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
            rlpStream.Encode(header.MixHash);
            rlpStream.Encode(header.Nonce, NonceLength);
        }

        rlpStream.Encode(header.Validators);
        rlpStream.Encode(header.Validator);
        rlpStream.Encode(header.Penalties);

        if (!header.BaseFeePerGas.IsZero) rlpStream.Encode(header.BaseFeePerGas);

    }

    public Rlp Encode(XdcBlockHeader? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            return Rlp.OfEmptySequence;
        }

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }

    private static int GetContentLength(XdcBlockHeader? item, RlpBehaviors rlpBehaviors)
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
            contentLength += Rlp.LengthOf(item.MixHash);
            contentLength += Rlp.LengthOfNonce(item.Nonce);
        }

        if (!item.BaseFeePerGas.IsZero) contentLength += Rlp.LengthOf(item.BaseFeePerGas);
        return contentLength;
    }

    public int GetLength(XdcBlockHeader? item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
}

