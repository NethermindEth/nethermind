// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Avalanche.Blocks;

/// <summary>
/// RLP encode/decode for the Avalanche C-Chain (Coreth) header, and the block-hash derivation
/// <c>blockHash = keccak256(RLP(header))</c>.
/// </summary>
/// <remarks>
/// Modeled closely on <see cref="Nethermind.Serialization.Rlp.HeaderDecoder"/>; the structural differences
/// from the Ethereum header are exactly:
/// <list type="bullet">
///   <item>an always-present <c>ExtDataHash</c> (a <c>gencodec:"required"</c> <see cref="Hash256"/>) inserted as
///   the 16th element, immediately after <c>Nonce</c> and <b>before</b> the optional <c>BaseFee</c>;</item>
///   <item>two extra <c>rlp:"optional"</c> integers, <c>ExtDataGasUsed</c> and <c>BlockGasCost</c>, inserted
///   between <c>BaseFee</c> and the Cancun-era blob/beacon optionals.</item>
/// </list>
/// The Go <c>rlp:"optional"</c> contract is replicated for the six optional fields: a trailing optional is
/// encoded only if it — or any later optional — is non-nil, and on decode any absent trailing optional defaults
/// to <c>null</c>. Coreth always writes <c>MixDigest</c> and <c>Nonce</c> (it has no AuRa-style alternative), so
/// those are unconditional here. The 16-field shape (genesis / Apricot Phase 2) carries only <c>ExtDataHash</c>;
/// AP3 adds <c>BaseFee</c>; AP4 adds <c>ExtDataGasUsed</c> + <c>BlockGasCost</c>; Cancun-era blocks add the
/// blob/beacon tail.
/// <para>
/// NOTE: current Coreth master appends two further <c>rlp:"optional"</c> Granite-era fields after
/// <c>ParentBeaconRoot</c> — <c>TimeMilliseconds</c> (<c>*uint64</c>) and <c>MinDelayExcess</c>. They are not
/// implemented here (this codec targets the pre-Granite shape up to and including <c>ParentBeaconRoot</c>). To
/// support Granite-era headers, add the two fields to the cascade after index 5, preserving order.
/// </para>
/// </remarks>
public sealed class AvalancheHeaderDecoder
{
    /// <summary>The fixed-length nonce field width, in bytes (identical to Ethereum).</summary>
    public const int NonceLength = HeaderDecoder.NonceLength;

    /// <summary>Number of trailing <c>rlp:"optional"</c> fields after the always-present prefix.</summary>
    private const int OptionalCount = 6;

    public static AvalancheHeaderDecoder Instance { get; } = new();

    /// <summary>Decodes a Coreth header from <paramref name="data"/>.</summary>
    public AvalancheBlockHeader? Decode(ReadOnlySpan<byte> data, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader reader = new(data);
        return Decode(ref reader, rlpBehaviors);
    }

    /// <inheritdoc cref="Decode(System.ReadOnlySpan{byte},Nethermind.Serialization.Rlp.RlpBehaviors)"/>
    public AvalancheBlockHeader? Decode(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (reader.IsNextItemEmptyList())
        {
            reader.ReadByte();
            return null;
        }

        ReadOnlySpan<byte> headerRlp = reader.PeekNextItem();
        int headerSequenceLength = reader.ReadSequenceLength();
        int headerCheck = reader.Position + headerSequenceLength;

        Hash256? parentHash = reader.DecodeKeccak();
        Hash256? unclesHash = reader.DecodeKeccak();
        Address? beneficiary = reader.DecodeAddress();
        Hash256? stateRoot = reader.DecodeKeccak();
        Hash256? transactionsRoot = reader.DecodeKeccak();
        Hash256? receiptsRoot = reader.DecodeKeccak();
        Bloom? bloom = reader.DecodeBloom();
        UInt256 difficulty = reader.DecodeUInt256();
        ulong number = reader.DecodeULong();
        ulong gasLimit = reader.DecodeULong();
        ulong gasUsed = reader.DecodeULong();
        ulong timestamp = reader.DecodeULong();
        byte[] extraData = reader.DecodeByteArray();

        AvalancheBlockHeader header = new(
            parentHash!,
            unclesHash!,
            beneficiary!,
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
            MixHash = reader.DecodeKeccak(),
            Nonce = (ulong)reader.DecodeUInt256(NonceLength),
            // gencodec:"required": always present since genesis, decoded as a plain 32-byte hash.
            ExtDataHash = reader.DecodeKeccak()
        };

        // Six trailing rlp:"optional" fields. Each is present only if the reader has not yet reached the end of
        // the header sequence; an absent trailing optional leaves the corresponding field null/default.
        if (reader.Position != headerCheck) header.BaseFeePerGas = reader.DecodeUInt256();
        if (reader.Position != headerCheck) header.ExtDataGasUsed = reader.DecodeUInt256();
        if (reader.Position != headerCheck) header.BlockGasCost = reader.DecodeUInt256();
        if (reader.Position != headerCheck) header.BlobGasUsed = reader.DecodeULong();
        if (reader.Position != headerCheck) header.ExcessBlobGas = reader.DecodeULong();
        if (reader.Position != headerCheck) header.ParentBeaconBlockRoot = reader.DecodeKeccak();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            reader.Check(headerCheck);
        }

        header.Hash = Keccak.Compute(headerRlp);
        return header;
    }

    /// <summary>Encodes <paramref name="header"/> into a freshly allocated buffer.</summary>
    public byte[] Encode(AvalancheBlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (header is null)
        {
            return [Rlp.EmptyListByte];
        }

        byte[] buffer = new byte[GetLength(header, rlpBehaviors)];
        RlpWriter writer = new(buffer);
        Encode(ref writer, header, rlpBehaviors);
        return buffer;
    }

    /// <summary>Writes <paramref name="header"/> into <paramref name="writer"/>.</summary>
    public void Encode<TWriter>(ref TWriter writer, AvalancheBlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (header is null)
        {
            writer.EncodeNullObject();
            return;
        }

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
        writer.Encode(header.MixHash);
        writer.Encode(header.Nonce, NonceLength);
        writer.Encode(header.ExtDataHash);

        Span<bool> required = stackalloc bool[OptionalCount];
        ComputeOptionalCascade(header, required);

        if (required[0]) writer.Encode(header.BaseFeePerGas);
        if (required[1]) writer.Encode(header.ExtDataGasUsed.GetValueOrDefault());
        if (required[2]) writer.Encode(header.BlockGasCost.GetValueOrDefault());
        if (required[3]) writer.Encode(header.BlobGasUsed.GetValueOrDefault());
        if (required[4]) writer.Encode(header.ExcessBlobGas.GetValueOrDefault());
        if (required[5]) writer.Encode(header.ParentBeaconBlockRoot);
    }

    /// <summary>The total encoded length of <paramref name="header"/>, including the list header.</summary>
    public int GetLength(AvalancheBlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => header is null ? Rlp.OfEmptyList.Length : Rlp.LengthOfSequence(GetContentLength(header, rlpBehaviors));

    /// <summary>Computes the C-Chain block hash <c>keccak256(RLP(header))</c>.</summary>
    public Hash256 ComputeHash(AvalancheBlockHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        KeccakRlpWriter writer = new();
        Encode(ref writer, header);
        return writer.GetHash();
    }

    private static int GetContentLength(AvalancheBlockHeader header, RlpBehaviors rlpBehaviors)
    {
        int contentLength = Rlp.LengthOf(header.ParentHash)
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
                            + Rlp.LengthOfNonce(header.Nonce)
                            + Rlp.LengthOf(header.ExtDataHash);

        Span<bool> required = stackalloc bool[OptionalCount];
        ComputeOptionalCascade(header, required);

        if (required[0]) contentLength += Rlp.LengthOf(header.BaseFeePerGas);
        if (required[1]) contentLength += Rlp.LengthOf(header.ExtDataGasUsed.GetValueOrDefault());
        if (required[2]) contentLength += Rlp.LengthOf(header.BlockGasCost.GetValueOrDefault());
        if (required[3]) contentLength += Rlp.LengthOf(header.BlobGasUsed.GetValueOrDefault());
        if (required[4]) contentLength += Rlp.LengthOf(header.ExcessBlobGas.GetValueOrDefault());
        if (required[5]) contentLength += Rlp.LengthOf(header.ParentBeaconBlockRoot);

        return contentLength;
    }

    /// <summary>
    /// Fills <paramref name="required"/> with the Go <c>rlp:"optional"</c> presence flags for the six trailing
    /// optionals, in field order <c>[BaseFee, ExtDataGasUsed, BlockGasCost, BlobGasUsed, ExcessBlobGas,
    /// ParentBeaconRoot]</c>.
    /// </summary>
    /// <remarks>
    /// A trailing optional is serialized only when it — or any later optional — carries a value. The presence of
    /// any field therefore forces every preceding optional to be written too (as its zero/default value),
    /// preserving positional decoding. <c>BaseFeePerGas</c> is a non-nullable field, so it is "set" whenever it
    /// is non-zero.
    /// </remarks>
    private static void ComputeOptionalCascade(AvalancheBlockHeader header, Span<bool> required)
    {
        required[0] = !header.BaseFeePerGas.IsZero;
        required[1] = header.ExtDataGasUsed is not null;
        required[2] = header.BlockGasCost is not null;
        required[3] = header.BlobGasUsed is not null;
        required[4] = header.ExcessBlobGas is not null;
        required[5] = header.ParentBeaconBlockRoot is not null;

        for (int i = OptionalCount - 2; i >= 0; i--)
        {
            required[i] |= required[i + 1];
        }
    }
}
