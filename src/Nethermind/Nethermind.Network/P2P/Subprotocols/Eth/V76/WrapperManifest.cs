// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V75;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76;

/// <summary>An <c>eth/76</c> wrapper manifest <c>[txhash, ...]</c>: the strictly ascending consensus hashes of all a wrapper's
/// transactions, whatever its mode and transaction count.</summary>
/// <remarks>A manifest is its sender's claim alone until checked against the wrapper.</remarks>
public sealed class WrapperManifest : IEquatable<WrapperManifest>
{
    private WrapperManifest(byte[] encoded, ValueHash256[] hashes)
    {
        Encoded = encoded;
        Hashes = hashes;
    }

    /// <summary>The canonical RLP encoding.</summary>
    public byte[] Encoded { get; }

    /// <summary>The consensus transaction hashes, strictly ascending.</summary>
    public ValueHash256[] Hashes { get; }

    /// <summary>Keccak-256 of the concatenated hashes, which an announcement carries as <c>txs-hash</c>.</summary>
    public ValueHash256 TransactionsDigest => ValueKeccak.Compute(MemoryMarshal.AsBytes(Hashes.AsSpan()));

    /// <summary>The manifest of a wrapper whose transactions have <paramref name="hashes"/>, in wrapper order.</summary>
    public static WrapperManifest Of(ValueHash256[] hashes)
    {
        byte[][] items = new byte[hashes.Length][];
        for (int i = 0; i < items.Length; i++) items[i] = Eth75Rlp.EncodeBytes(hashes[i].Bytes);
        return new(Eth75Rlp.EncodeList(items), hashes);
    }

    /// <summary>Checks a received manifest: a nonempty, strictly ascending list of 32-byte hashes.</summary>
    /// <exception cref="RlpException">The manifest is malformed.</exception>
    public static WrapperManifest Decode(ReadOnlySpan<byte> encoded)
    {
        Eth75RlpReader outer = new(encoded);
        Eth75RlpReader list = outer.ReadList();
        outer.End();
        ValueHash256[] hashes = new ValueHash256[list.CountRemaining(int.MaxValue)];
        if (hashes.Length == 0) throw new RlpException("A manifest lists no transactions");
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = list.ReadHash();
            if (i > 0 && hashes[i - 1].CompareTo(hashes[i]) >= 0) throw new RlpException("Manifest hashes are not strictly ascending");
        }
        return new(encoded.ToArray(), hashes);
    }

    public bool Equals(WrapperManifest? other) => other is not null && Encoded.AsSpan().SequenceEqual(other.Encoded);

    public override bool Equals(object? obj) => Equals(obj as WrapperManifest);

    public override int GetHashCode() => Hashes[0].GetHashCode();
}
