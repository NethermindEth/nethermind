// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Avalanche.Blocks;

/// <summary>
/// RLP encode/decode for the Avalanche C-Chain (Coreth) block and body.
/// </summary>
/// <remarks>
/// The Coreth <c>extblock</c> wire format is the five-element list
/// <c>[Header, [Txs...], [Uncles...], Version, ExtData]</c>, and the body format drops the header:
/// <c>[[Txs...], [Uncles...], Version, ExtData]</c>. Transactions reuse Nethermind's
/// <see cref="TxDecoder"/> (block format: legacy txs raw, typed txs wrapped in an RLP byte string), exactly as
/// go-ethereum/Coreth serialize them. <c>Version</c> is a <c>uint32</c>; <c>ExtData</c> is an RLP byte string
/// carrying the raw atomic-transaction bytes.
/// <para>
/// <c>ExtData</c> carries Go's <c>rlp:"nil"</c> tag. For a plain <c>[]byte</c> both a <c>nil</c> slice and an
/// empty slice serialize identically to the empty RLP byte string <c>0x80</c>, so the encoder treats
/// <c>null</c> and <c>[]</c> the same; on decode an empty byte string is materialized as an empty array.
/// </para>
/// </remarks>
public sealed class AvalancheBlockDecoder
{
    private readonly TxDecoder _txDecoder = TxDecoder.Instance;
    private readonly AvalancheHeaderDecoder _headerDecoder = AvalancheHeaderDecoder.Instance;

    public static AvalancheBlockDecoder Instance { get; } = new();

    /// <summary>Decodes a Coreth <c>extblock</c> from <paramref name="data"/>.</summary>
    public AvalancheBlock? Decode(ReadOnlySpan<byte> data, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader reader = new(data);
        return Decode(ref reader, rlpBehaviors);
    }

    /// <inheritdoc cref="Decode(System.ReadOnlySpan{byte},Nethermind.Serialization.Rlp.RlpBehaviors)"/>
    public AvalancheBlock? Decode(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (reader.IsNextItemEmptyList())
        {
            reader.ReadByte();
            return null;
        }

        int sequenceLength = reader.ReadSequenceLength();
        int blockCheck = reader.Position + sequenceLength;

        AvalancheBlockHeader header = _headerDecoder.Decode(ref reader)
                                      ?? throw new RlpException("Avalanche block is missing its header.");
        AvalancheBlockBody body = DecodeBodyComponents(ref reader);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            reader.Check(blockCheck);
        }

        return new AvalancheBlock(header, body);
    }

    /// <summary>Decodes a Coreth block body <c>[[Txs...], [Uncles...], Version, ExtData]</c>.</summary>
    public AvalancheBlockBody DecodeBody(ReadOnlySpan<byte> data, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader reader = new(data);
        return DecodeBody(ref reader, rlpBehaviors);
    }

    /// <inheritdoc cref="DecodeBody(System.ReadOnlySpan{byte},Nethermind.Serialization.Rlp.RlpBehaviors)"/>
    public AvalancheBlockBody DecodeBody(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int sequenceLength = reader.ReadSequenceLength();
        int bodyCheck = reader.Position + sequenceLength;

        AvalancheBlockBody body = DecodeBodyComponents(ref reader);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            reader.Check(bodyCheck);
        }

        return body;
    }

    /// <summary>Encodes a Coreth <c>extblock</c> into a freshly allocated buffer.</summary>
    public byte[] Encode(AvalancheBlock? block, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (block is null)
        {
            return [Rlp.EmptyListByte];
        }

        byte[] buffer = new byte[GetLength(block, rlpBehaviors)];
        RlpWriter writer = new(buffer);
        Encode(ref writer, block, rlpBehaviors);
        return buffer;
    }

    /// <summary>Writes a Coreth <c>extblock</c> into <paramref name="writer"/>.</summary>
    public void Encode<TWriter>(ref TWriter writer, AvalancheBlock? block, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (block is null)
        {
            writer.EncodeNullObject();
            return;
        }

        int headerLength = _headerDecoder.GetLength(block.Header);
        int txsLength = GetTxsLength(block.Transactions);
        int unclesLength = GetUnclesLength(block.Uncles);

        int contentLength = headerLength
                            + Rlp.LengthOfSequence(txsLength)
                            + Rlp.LengthOfSequence(unclesLength)
                            + Rlp.LengthOf(block.Version)
                            + Rlp.LengthOf(block.ExtData.AsSpan());

        writer.StartSequence(contentLength);
        _headerDecoder.Encode(ref writer, block.Header);
        EncodeBodyTail(ref writer, block.Transactions, block.Uncles, block.Version, block.ExtData, txsLength, unclesLength);
    }

    /// <summary>Encodes a Coreth block body into a freshly allocated buffer.</summary>
    public byte[] EncodeBody(AvalancheBlockBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        byte[] buffer = new byte[GetBodyLength(body)];
        RlpWriter writer = new(buffer);
        EncodeBody(ref writer, body);
        return buffer;
    }

    /// <summary>Writes a Coreth block body into <paramref name="writer"/>.</summary>
    public void EncodeBody<TWriter>(ref TWriter writer, AvalancheBlockBody body)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int txsLength = GetTxsLength(body.Transactions);
        int unclesLength = GetUnclesLength(body.Uncles);
        int contentLength = GetBodyContentLength(body, txsLength, unclesLength);

        writer.StartSequence(contentLength);
        EncodeBodyTail(ref writer, body.Transactions, body.Uncles, body.Version, body.ExtData, txsLength, unclesLength);
    }

    /// <summary>The total encoded length of <paramref name="block"/>, including the list header.</summary>
    public int GetLength(AvalancheBlock? block, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => block is null ? Rlp.OfEmptyList.Length : Rlp.LengthOfSequence(GetContentLength(block, rlpBehaviors));

    /// <summary>The total encoded length of <paramref name="body"/>, including the list header.</summary>
    public int GetBodyLength(AvalancheBlockBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        int txsLength = GetTxsLength(body.Transactions);
        int unclesLength = GetUnclesLength(body.Uncles);
        return Rlp.LengthOfSequence(GetBodyContentLength(body, txsLength, unclesLength));
    }

    private AvalancheBlockBody DecodeBodyComponents(ref RlpReader reader)
    {
        Transaction[] transactions = reader.DecodeArray<Transaction>(_txDecoder);
        BlockHeader[] uncles = DecodeUncles(ref reader);
        uint version = reader.DecodeUInt();
        byte[] extData = reader.DecodeByteArray();

        return new AvalancheBlockBody(transactions, uncles, version, extData);
    }

    /// <summary>
    /// Decodes the uncles sequence. Uncles are always empty on the C-Chain, but a non-empty sequence round-trips
    /// each entry through <see cref="AvalancheHeaderDecoder"/>.
    /// </summary>
    private BlockHeader[] DecodeUncles(ref RlpReader reader)
    {
        int sequenceLength = reader.ReadSequenceLength();
        if (sequenceLength == 0)
        {
            return [];
        }

        int unclesCheck = reader.Position + sequenceLength;
        int count = reader.PeekNumberOfItemsRemaining(unclesCheck);
        BlockHeader[] uncles = new BlockHeader[count];
        for (int i = 0; i < count; i++)
        {
            uncles[i] = _headerDecoder.Decode(ref reader)
                        ?? throw new RlpException("Null uncle header in Avalanche block body.");
        }

        reader.Check(unclesCheck);
        return uncles;
    }

    private void EncodeBodyTail<TWriter>(
        ref TWriter writer,
        Transaction[] transactions,
        BlockHeader[] uncles,
        uint version,
        byte[]? extData,
        int txsLength,
        int unclesLength)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(txsLength);
        for (int i = 0; i < transactions.Length; i++)
        {
            _txDecoder.Encode(ref writer, transactions[i]);
        }

        writer.StartSequence(unclesLength);
        for (int i = 0; i < uncles.Length; i++)
        {
            EncodeUncle(ref writer, uncles[i]);
        }

        writer.Encode(version);
        // rlp:"nil": both a nil and an empty slice serialize to the empty RLP byte string (0x80).
        writer.Encode(extData.AsSpan());
    }

    private int GetContentLength(AvalancheBlock block, RlpBehaviors rlpBehaviors)
    {
        int txsLength = GetTxsLength(block.Transactions);
        int unclesLength = GetUnclesLength(block.Uncles);

        return _headerDecoder.GetLength(block.Header, rlpBehaviors)
               + Rlp.LengthOfSequence(txsLength)
               + Rlp.LengthOfSequence(unclesLength)
               + Rlp.LengthOf(block.Version)
               + Rlp.LengthOf(block.ExtData.AsSpan());
    }

    private int GetBodyContentLength(AvalancheBlockBody body, int txsLength, int unclesLength)
        => Rlp.LengthOfSequence(txsLength)
           + Rlp.LengthOfSequence(unclesLength)
           + Rlp.LengthOf(body.Version)
           + Rlp.LengthOf(body.ExtData.AsSpan());

    private int GetTxsLength(Transaction[] transactions)
    {
        int sum = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            sum += _txDecoder.GetLength(transactions[i], RlpBehaviors.None);
        }

        return sum;
    }

    private int GetUnclesLength(BlockHeader[] uncles)
    {
        int sum = 0;
        for (int i = 0; i < uncles.Length; i++)
        {
            sum += GetUncleLength(uncles[i]);
        }

        return sum;
    }

    private int GetUncleLength(BlockHeader uncle)
        => uncle is AvalancheBlockHeader avalancheUncle
            ? _headerDecoder.GetLength(avalancheUncle)
            : throw new RlpException("Avalanche uncles must be Avalanche headers.");

    private void EncodeUncle<TWriter>(ref TWriter writer, BlockHeader uncle)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (uncle is not AvalancheBlockHeader avalancheUncle)
        {
            throw new RlpException("Avalanche uncles must be Avalanche headers.");
        }

        _headerDecoder.Encode(ref writer, avalancheUncle);
    }
}
