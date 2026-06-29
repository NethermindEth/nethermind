// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Avalanche.Parity;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Parity;

/// <summary>
/// <c>ExtDataHash = keccak256(RLP(extdata))</c>; empty/nil extdata yields <c>keccak256(RLP("")) = keccak256(0x80)</c>.
/// </summary>
public class AvalancheExtDataTests
{
    // keccak256(0x80) — the keccak of an empty RLP byte string (== Ethereum's empty-trie hash).
    private const string EmptyExtDataHashHex = "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421";

    [Test]
    public void EmptyExtDataHash_is_keccak_of_0x80()
        => Assert.That(AvalancheExtData.EmptyExtDataHash, Is.EqualTo(new ValueHash256(EmptyExtDataHashHex)));

    [Test]
    public void CalcExtDataHash_empty_returns_empty_constant()
        => Assert.That(AvalancheExtData.CalcExtDataHash(ReadOnlySpan<byte>.Empty), Is.EqualTo(AvalancheExtData.EmptyExtDataHash));

    // RLP of a >55-byte string is 0xb7+lengthOfLength, then the big-endian length, then the bytes.
    // Here a 64-byte payload of 0xab => prefix 0xb8 0x40 followed by 64 * 0xab.
    [Test]
    public void CalcExtDataHash_nonempty_matches_keccak_of_rlp_byte_string()
    {
        byte[] extData = new byte[64];
        Array.Fill(extData, (byte)0xab);

        // Independently build RLP(extData) as a byte string: long-string header (0xb8, len=0x40) + payload.
        byte[] rlp = new byte[2 + extData.Length];
        rlp[0] = 0xb8;
        rlp[1] = (byte)extData.Length;
        extData.CopyTo(rlp.AsSpan(2));
        ValueHash256 expected = ValueKeccak.Compute(rlp);

        Assert.That(AvalancheExtData.CalcExtDataHash(extData), Is.EqualTo(expected));
        // Sanity: non-empty extdata must not collide with the empty constant.
        Assert.That(AvalancheExtData.CalcExtDataHash(extData), Is.Not.EqualTo(AvalancheExtData.EmptyExtDataHash));
    }

    // A single byte >= 0x80 RLP-encodes as 0x81 <byte>.
    [Test]
    public void CalcExtDataHash_single_high_byte_matches_rlp_string_encoding()
    {
        byte[] extData = [0xff];

        byte[] rlp = [0x81, 0xff];
        ValueHash256 expected = ValueKeccak.Compute(rlp);

        Assert.That(AvalancheExtData.CalcExtDataHash(extData), Is.EqualTo(expected));
    }
}
