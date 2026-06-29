// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Avalanche.Parity;

/// <summary>
/// Encodes and decodes <see cref="AvalancheStateAccount"/> byte-for-byte with Coreth/libevm, including the
/// always-present trailing <c>isMultiCoin</c> boolean.
/// </summary>
/// <remarks>
/// The list layout is <c>[nonce, balance, root, codeHash, isMultiCoin]</c>. A vanilla four-field Ethereum
/// account encodes with header byte <c>0xed</c>; adding the fifth boolean shifts it to <c>0xee</c> with a
/// trailing <c>0x80</c> (false) or <c>0x01</c> (true). Modeled on
/// <see cref="Nethermind.Serialization.Rlp.AccountDecoder"/>, but the storage root and code hash are encoded
/// as raw byte strings so that arbitrary-length placeholder values round-trip exactly; the singleton-keccak
/// collapsing that <see cref="RlpReader.DecodeKeccak"/> performs is deliberately avoided.
/// </remarks>
public sealed class AvalancheStateAccountDecoder
{
    public static AvalancheStateAccountDecoder Instance { get; } = new();

    /// <summary>Encodes <paramref name="account"/> into a freshly allocated buffer.</summary>
    public byte[] Encode(in AvalancheStateAccount account)
    {
        int contentLength = GetContentLength(account);
        byte[] buffer = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(buffer);
        Encode(ref writer, account, contentLength);
        return buffer;
    }

    /// <summary>Writes <paramref name="account"/> into <paramref name="writer"/>.</summary>
    public void Encode<TWriter>(ref TWriter writer, in AvalancheStateAccount account, int? contentLength = null)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int length = contentLength ?? GetContentLength(account);

        writer.StartSequence(length);
        writer.Encode(account.Nonce);
        writer.Encode(account.Balance);
        writer.Encode((ReadOnlySpan<byte>)account.StorageRoot);
        writer.Encode((ReadOnlySpan<byte>)account.CodeHash);
        writer.Encode(account.IsMultiCoin);
    }

    /// <summary>Decodes a Coreth-format account, including the trailing <c>isMultiCoin</c> boolean.</summary>
    public AvalancheStateAccount Decode(ReadOnlySpan<byte> data)
    {
        RlpReader reader = new(data);
        return Decode(ref reader);
    }

    /// <inheritdoc cref="Decode(System.ReadOnlySpan{byte})"/>
    public AvalancheStateAccount Decode(ref RlpReader reader)
    {
        reader.ReadSequenceLength();

        ulong nonce = reader.DecodeULong();
        UInt256 balance = reader.DecodeUInt256();
        byte[] storageRoot = reader.DecodeByteArray();
        byte[] codeHash = reader.DecodeByteArray();
        bool isMultiCoin = reader.DecodeBool();

        return new AvalancheStateAccount(nonce, balance, storageRoot, codeHash, isMultiCoin);
    }

    /// <summary>The length of the RLP list content (excluding the list header).</summary>
    public int GetContentLength(in AvalancheStateAccount account)
    {
        int contentLength = Rlp.LengthOf(account.Nonce);
        contentLength += Rlp.LengthOf(account.Balance);
        contentLength += Rlp.LengthOf((ReadOnlySpan<byte>)account.StorageRoot);
        contentLength += Rlp.LengthOf((ReadOnlySpan<byte>)account.CodeHash);
        contentLength += Rlp.LengthOf(account.IsMultiCoin);
        return contentLength;
    }

    /// <summary>The total encoded length, including the list header.</summary>
    public int GetLength(in AvalancheStateAccount account) => Rlp.LengthOfSequence(GetContentLength(account));
}
