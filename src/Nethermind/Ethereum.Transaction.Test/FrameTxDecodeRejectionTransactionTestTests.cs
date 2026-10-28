// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Ethereum.Transaction.Test;

/// <summary>
/// Guards the <c>transaction_tests</c> labels that EIP-8141 fixtures expect for frame transactions the decoder rejects.
/// </summary>
/// <remarks>
/// Each case runs raw bytes through the harness, so it covers both the decoder's wording and the label table. The
/// last cases name a label the decoder's message does not support, and must keep failing.
/// </remarks>
public class FrameTxDecodeRejectionTransactionTestTests : TransactionTestBase
{
    private const string Fork = "Eip8141Prototype";

    /// <summary>2^64 as a nine-byte integer, one byte wider than the nonce field.</summary>
    private static readonly Rlp NonceOf2Pow64 = new([0x89, 0x01, 0, 0, 0, 0, 0, 0, 0, 0]);

    /// <summary>A zero with a leading zero byte: the single byte 0x00 instead of the empty string 0x80.</summary>
    private static readonly Rlp ZeroWithLeadingZero = new([0x00]);

    [TestCase("0x06", "TransactionException.RLP_ERROR_EOF", TestName = "The type byte alone is RLP_ERROR_EOF")]
    [TestCase("0x06c0", "TransactionException.RLP_TOO_FEW_ELEMENTS", TestName = "An empty payload list is RLP_TOO_FEW_ELEMENTS")]
    public void TruncatedPayloadIsRejectedWithItsLabel(string txBytes, string expectedException)
    {
        Result result = RunTest(TestFor(txBytes, expectedException));

        Assert.That((bool)result, Is.True, result.Error);
    }

    [Test]
    public void NonceWiderThan64BitsIsNonceOverflow()
    {
        Result result = RunTest(TestFor(TxBytes(nonce: NonceOf2Pow64), "TransactionException.NONCE_OVERFLOW"));

        Assert.That((bool)result, Is.True, result.Error);
    }

    [Test]
    public void PriorityFeeWithLeadingZeroIsLeadingZerosPriorityFee()
    {
        Result result = RunTest(TestFor(TxBytes(maxPriorityFeePerGas: ZeroWithLeadingZero),
            "TransactionException.RLP_LEADING_ZEROS_PRIORITY_FEE"));

        Assert.That((bool)result, Is.True, result.Error);
    }

    [Test]
    public void NonceWiderThan64BitsIsNotNonceTooBig()
    {
        Result result = RunTest(TestFor(TxBytes(nonce: NonceOf2Pow64), "TransactionException.NONCE_TOO_BIG"));

        Assert.That((bool)result, Is.False);
    }

    [Test]
    public void TypeByteAloneIsNotAnInvalidSignature()
    {
        Result result = RunTest(TestFor("0x06", "TransactionException.TYPE_6_INVALID_SIGNATURE"));

        Assert.That((bool)result, Is.False);
    }

    /// <summary>A frame transaction with no frames, varying only the fields the caller supplies.</summary>
    private static string TxBytes(Rlp nonce = null, Rlp maxPriorityFeePerGas = null)
    {
        Rlp payload = Rlp.Encode(
            Rlp.Encode(1L),                                 // chain_id
            nonce ?? Rlp.Encode(0L),
            Rlp.Encode(TestItem.AddressA.Bytes),            // sender
            Rlp.Encode(Array.Empty<Rlp>()),                 // frames
            Rlp.Encode(Array.Empty<Rlp>()),                 // signatures
            Rlp.Encode(                                     // fees
                Rlp.Encode(0L),                             // max_fee
                maxPriorityFeePerGas ?? Rlp.Encode(0L)),
            Rlp.Encode(Array.Empty<Rlp>()));                // blob_versioned_hashes

        return Bytes.Concat((byte)TxType.FrameTx, payload.Bytes).ToHexString(true);
    }

    private static TransactionTest TestFor(string txBytes, string expectedException) =>
        new()
        {
            Name = TestContext.CurrentContext.Test.Name,
            Fork = Fork,
            TxBytes = txBytes,
            ExpectedException = expectedException,
        };
}
