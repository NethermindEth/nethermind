// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Avalanche.Parity;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Parity;

/// <summary>
/// Byte-for-byte parity with libevm's <c>core/types/state_account.libevm_test.go</c> StateAccount RLP
/// vectors (github.com/ava-labs/libevm). The account under test is
/// <c>{ Nonce: 0x444444, Balance: 0x666666, Root: 0x00..00 (32 bytes), CodeHash: 0xbbbbbb }</c>.
/// </summary>
public class AvalancheStateAccountDecoderTests
{
    // 32-byte all-zero root, encoded as an RLP string => a0 + 32 zero bytes.
    private const string ZeroRootHex = "0000000000000000000000000000000000000000000000000000000000000000";

    // 3-byte code hash placeholder used by the upstream vectors => 83 bbbbbb.
    private const string CodeHashHex = "bbbbbb";

    // Vanilla four-field Ethereum account (header 0xed), no isMultiCoin field. Decode-compat baseline.
    private const string VanillaHex =
        "ed8344444483666666a0000000000000000000000000000000000000000000000000000000000000000083bbbbbb";

    // isMultiCoin = false: header shifts to 0xee, trailing 0x80.
    private const string MultiCoinFalseHex =
        "ee8344444483666666a0000000000000000000000000000000000000000000000000000000000000000083bbbbbb80";

    // isMultiCoin = true: header 0xee, trailing 0x01.
    private const string MultiCoinTrueHex =
        "ee8344444483666666a0000000000000000000000000000000000000000000000000000000000000000083bbbbbb01";

    private static AvalancheStateAccount BuildAccount(bool isMultiCoin) => new(
        nonce: 0x444444,
        balance: (UInt256)0x666666,
        storageRoot: Bytes.FromHexString(ZeroRootHex),
        codeHash: Bytes.FromHexString(CodeHashHex),
        isMultiCoin: isMultiCoin);

    [TestCase(false, MultiCoinFalseHex)]
    [TestCase(true, MultiCoinTrueHex)]
    public void Encodes_byte_exact_with_libevm(bool isMultiCoin, string expectedHex)
    {
        AvalancheStateAccount account = BuildAccount(isMultiCoin);

        byte[] encoded = AvalancheStateAccountDecoder.Instance.Encode(account);

        Assert.That(encoded.ToHexString(), Is.EqualTo(expectedHex));
    }

    [TestCase(MultiCoinFalseHex, false)]
    [TestCase(MultiCoinTrueHex, true)]
    public void Decodes_libevm_vector(string hex, bool expectedIsMultiCoin)
    {
        AvalancheStateAccount account = AvalancheStateAccountDecoder.Instance.Decode(Bytes.FromHexString(hex));

        Assert.That(account.Nonce, Is.EqualTo((ulong)0x444444));
        Assert.That(account.Balance, Is.EqualTo((UInt256)0x666666));
        Assert.That(account.StorageRoot.ToHexString(), Is.EqualTo(ZeroRootHex));
        Assert.That(account.CodeHash.ToHexString(), Is.EqualTo(CodeHashHex));
        Assert.That(account.IsMultiCoin, Is.EqualTo(expectedIsMultiCoin));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Round_trips(bool isMultiCoin)
    {
        AvalancheStateAccount original = BuildAccount(isMultiCoin);

        byte[] encoded = AvalancheStateAccountDecoder.Instance.Encode(original);
        AvalancheStateAccount decoded = AvalancheStateAccountDecoder.Instance.Decode(encoded);

        Assert.That(decoded, Is.EqualTo(original));
    }

    [Test]
    public void Header_byte_shifts_from_0xed_to_0xee_when_isMultiCoin_present()
    {
        // The only structural difference between a vanilla account and the Avalanche account is the trailing
        // boolean: the list header grows by one item (0xed -> 0xee) and one byte (0x80 / 0x01) is appended.
        byte[] vanilla = Bytes.FromHexString(VanillaHex);
        byte[] avalancheFalse = AvalancheStateAccountDecoder.Instance.Encode(BuildAccount(isMultiCoin: false));

        Assert.That(vanilla[0], Is.EqualTo((byte)0xed));
        Assert.That(avalancheFalse[0], Is.EqualTo((byte)0xee));
        // First four fields are identical; the Avalanche encoding is the vanilla content plus a trailing 0x80.
        Assert.That(avalancheFalse.Length, Is.EqualTo(vanilla.Length + 1));
        Assert.That(avalancheFalse[^1], Is.EqualTo((byte)0x80));
        Assert.That(avalancheFalse.AsSpan(1, vanilla.Length - 1).ToArray(), Is.EqualTo(vanilla.AsSpan(1).ToArray()));
    }
}
