// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V75;

/// <summary>EIP-8437 transaction metadata <c>[txtypes, [txsize, ...], [txhash, ...], [txsource, ...], [txnonce, ...],
/// [txfees, ...], [txgas, ...]]</c> of a wrapper's transactions, in wrapper order.</summary>
/// <remarks>Held in its canonical encoding, so a claim matches what a wrapper's transactions give exactly when the bytes are
/// equal. Metadata establishes neither coverage nor validity.</remarks>
public sealed class WrapperMetadata
{
    private const int Fields = 7;
    private const int SourceLength = 20;
    private const int MaxFeeBytes = 32;

    private WrapperMetadata(byte[] encoded, ValueHash256[] hashes)
    {
        Encoded = encoded;
        Hashes = hashes;
    }

    /// <summary>The canonical RLP encoding.</summary>
    public byte[] Encoded { get; }

    /// <summary>The consensus transaction hashes, strictly ascending.</summary>
    public ValueHash256[] Hashes { get; }

    public int Count => Hashes.Length;

    private byte[][]? _identities;

    /// <summary>Whether transaction <paramref name="index"/> is described with the type, source, nonce, fees and frame gas that
    /// transaction <paramref name="otherIndex"/> of <paramref name="other"/> is.</summary>
    /// <remarks>The pooled size is not compared: a consensus transaction hash does not bind it (EIP-8437).</remarks>
    internal bool DescribesAlike(int index, WrapperMetadata other, int otherIndex) =>
        Identities[index].AsSpan().SequenceEqual(other.Identities[otherIndex]);

    /// <summary>Per transaction, its type byte followed by its canonical source, nonce, fees and gas items.</summary>
    private byte[][] Identities => _identities ??= ReadIdentities();

    private byte[][] ReadIdentities()
    {
        Eth75RlpReader outer = new(Encoded);
        Eth75RlpReader fields = outer.ReadList();
        ReadOnlySpan<byte> types = fields.ReadBytes();
        fields.ReadList();
        fields.ReadList();
        Eth75RlpReader sources = fields.ReadList(), nonces = fields.ReadList(), fees = fields.ReadList(), gas = fields.ReadList();
        byte[][] identities = new byte[Count][];
        for (int i = 0; i < identities.Length; i++)
            identities[i] = [types[i], .. sources.ReadItem(), .. nonces.ReadItem(), .. fees.ReadItem(), .. gas.ReadItem()];
        return identities;
    }

    /// <summary>Checks received metadata against the EIP-8437 wire rules.</summary>
    /// <param name="encoded">The metadata list.</param>
    /// <param name="announcedLength">The announced wrapper length, which the transaction sizes must sum to less than.</param>
    /// <param name="maxTransactions">The most transactions the metadata may describe.</param>
    /// <exception cref="RlpException">The metadata is malformed.</exception>
    public static WrapperMetadata Decode(ReadOnlySpan<byte> encoded, int announcedLength, int maxTransactions)
    {
        Eth75RlpReader outer = new(encoded);
        Eth75RlpReader fields = outer.ReadList();
        outer.End();
        int count = fields.ReadBytes().Length;
        if (count == 0 || count > maxTransactions) throw new RlpException("Transaction metadata count is out of bounds");

        Eth75RlpReader sizes = ReadField(ref fields, count);
        long total = 0;
        while (sizes.HasMore)
        {
            ulong size = sizes.ReadUInt64();
            if (size == 0) throw new RlpException("Transaction metadata size is zero");
            total += (long)Math.Min(size, int.MaxValue);
        }
        if (total >= announcedLength) throw new RlpException("Transaction metadata sizes do not fit the announced length");

        Eth75RlpReader hashList = ReadField(ref fields, count);
        ValueHash256[] hashes = new ValueHash256[count];
        for (int i = 0; i < count; i++)
        {
            hashes[i] = hashList.ReadHash();
            if (i > 0 && hashes[i - 1].CompareTo(hashes[i]) >= 0) throw new RlpException("Transaction metadata hashes are not strictly ascending");
        }

        Eth75RlpReader sources = ReadField(ref fields, count);
        while (sources.HasMore)
            if (sources.ReadBytes().Length != SourceLength) throw new RlpException("Transaction metadata source must be 20 bytes");

        Eth75RlpReader nonces = ReadField(ref fields, count);
        while (nonces.HasMore) nonces.ReadUInt64();

        Eth75RlpReader fees = ReadField(ref fields, count);
        while (fees.HasMore)
        {
            Eth75RlpReader fee = fees.ReadList();
            while (fee.HasMore) fee.ReadInteger(MaxFeeBytes);
        }

        Eth75RlpReader gas = ReadField(ref fields, count);
        while (gas.HasMore) gas.ReadUInt64();
        fields.End();
        return new(encoded.ToArray(), hashes);
    }

    private static Eth75RlpReader ReadField(ref Eth75RlpReader fields, int count)
    {
        Eth75RlpReader field = fields.ReadList();
        if (field.CountRemaining(count) != count) throw new RlpException("Transaction metadata fields describe different transaction counts");
        return field;
    }

    /// <summary>The inline metadata an announcement of <paramref name="wrapper"/> carries: that of every transaction of a mode-0
    /// wrapper of at most <see cref="Eth75Protocol.MaxInlineTransactions"/> transactions, otherwise none.</summary>
    /// <exception cref="RlpException">A transaction of such a wrapper is not a well-formed EIP-8141 encoding.</exception>
    public static WrapperMetadata? InlineFor(ReadOnlySpan<byte> wrapper)
    {
        Eth75RlpReader outer = new(wrapper);
        Eth75RlpReader fields = outer.ReadList();
        Eth75RlpReader transactions = fields.ReadList();
        if (fields.ReadUInt64() != MempoolWrapper.ModeDirect || transactions.CountRemaining(int.MaxValue) > Eth75Protocol.MaxInlineTransactions)
            return null;
        return Of(wrapper);
    }

    /// <summary>The metadata of every transaction of a complete wrapper.</summary>
    /// <exception cref="RlpException">A transaction is not a well-formed EIP-8141 encoding.</exception>
    public static WrapperMetadata Of(ReadOnlySpan<byte> wrapper) => Of(wrapper, FrameGas);

    /// <param name="frameGas">The summed frame gas limits of a transaction payload's <c>frames</c> list.</param>
    internal static WrapperMetadata Of(ReadOnlySpan<byte> wrapper, FrameGasReader frameGas)
    {
        Eth75RlpReader outer = new(wrapper);
        Eth75RlpReader transactions = outer.ReadList().ReadList();
        List<byte> types = [];
        List<byte[]> sizes = [], hashItems = [], sources = [], nonces = [], fees = [], gas = [];
        List<ValueHash256> hashes = [];
        while (transactions.HasMore)
        {
            ReadOnlySpan<byte> encoding = transactions.ReadBytes();
            ValueHash256 hash = WrapperBodies.TransactionHash(encoding);
            types.Add(encoding[0]);
            sizes.Add(Eth75Rlp.EncodeUInt((ulong)encoding.Length));
            hashes.Add(hash);
            hashItems.Add(Eth75Rlp.EncodeBytes(hash.Bytes));

            Eth75RlpReader envelope = new(encoding[1..]);
            Eth75RlpReader payload = envelope.ReadList();
            // A blob-carrying transaction's pooled form wraps the payload with its sidecar.
            Eth75RlpReader body = payload.IsListNext ? payload.ReadList() : payload;
            body.ReadItem();
            // EIP-8250 keyed nonces put nonce_keys before nonce_seq, which is announced as the nonce.
            if (body.IsListNext) body.ReadItem();
            nonces.Add(Eth75Rlp.EncodeUInt(body.ReadUInt64()));
            ReadOnlySpan<byte> source = body.ReadBytes();
            if (source.Length != SourceLength) throw new RlpException("Frame transaction sender must be 20 bytes");
            sources.Add(Eth75Rlp.EncodeBytes(source));
            Eth75RlpReader frames = body.ReadList();
            gas.Add(Eth75Rlp.EncodeUInt(frameGas(ref frames)));
            body.ReadItem();
            Eth75RlpReader feeList = body.ReadList();
            List<byte[]> feeItems = [];
            while (feeList.HasMore) feeItems.Add(Eth75Rlp.EncodeBytes(feeList.ReadInteger(MaxFeeBytes)));
            fees.Add(Eth75Rlp.EncodeList([.. feeItems]));
        }
        byte[] encoded = Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes([.. types]), Eth75Rlp.EncodeList([.. sizes]), Eth75Rlp.EncodeList([.. hashItems]),
            Eth75Rlp.EncodeList([.. sources]), Eth75Rlp.EncodeList([.. nonces]), Eth75Rlp.EncodeList([.. fees]), Eth75Rlp.EncodeList([.. gas]));
        return new(encoded, [.. hashes]);
    }

    internal delegate ulong FrameGasReader(ref Eth75RlpReader frames);

    /// <summary>EIP-8437 <c>txgas</c>: the sum of the frames' execution and state gas limits, saturating as
    /// <see cref="FrameTxValidation.TotalGasLimit"/> does.</summary>
    private static ulong FrameGas(ref Eth75RlpReader frames)
    {
        ulong total = 0;
        while (frames.HasMore)
        {
            Eth75RlpReader frame = frames.ReadList();
            frame.ReadItem();
            frame.ReadItem();
            frame.ReadItem();
            Eth75RlpReader limits = frame.ReadList();
            ulong execution = limits.ReadUInt64();
            ulong state = limits.ReadUInt64();
            limits.End();
            total = total.SaturatingAdd(execution.SaturatingAdd(state));
        }
        return total;
    }

    public override bool Equals(object? obj) => obj is WrapperMetadata other && Encoded.AsSpan().SequenceEqual(other.Encoded);

    public override int GetHashCode() => Hashes.Length == 0 ? 0 : Hashes[0].GetHashCode();
}
