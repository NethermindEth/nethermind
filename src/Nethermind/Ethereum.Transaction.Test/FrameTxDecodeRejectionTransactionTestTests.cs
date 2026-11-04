// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

    private static IEnumerable<TestCaseData> LabelledRejectionCases()
    {
        yield return new TestCaseData("0x06", "TransactionException.RLP_ERROR_EOF")
            .SetName("The type byte alone is RLP_ERROR_EOF");
        yield return new TestCaseData("0x06c0", "TransactionException.RLP_TOO_FEW_ELEMENTS")
            .SetName("An empty payload list is RLP_TOO_FEW_ELEMENTS");

        Rlp withoutLastField = Rlp.Encode(
            Rlp.Encode(1L),                                 // chain_id
            Rlp.Encode(0L),                                 // nonce
            Rlp.Encode(TestItem.AddressA.Bytes),            // sender
            Rlp.Encode(Array.Empty<Rlp>()),                 // frames
            Rlp.Encode(Array.Empty<Rlp>()),                 // signatures
            Rlp.Encode(Rlp.Encode(0L), Rlp.Encode(0L)));    // fees; no blob_versioned_hashes
        yield return new TestCaseData(Bytes.Concat((byte)TxType.FrameTx, withoutLastField.Bytes).ToHexString(true),
                "TransactionException.RLP_TOO_FEW_ELEMENTS")
            .SetName("A payload without its last field is RLP_TOO_FEW_ELEMENTS");
        yield return new TestCaseData(FrameTxPayload.Encode(nonce: NonceOf2Pow64).ToHexString(true),
                "TransactionException.NONCE_OVERFLOW")
            .SetName("A nonce wider than 64 bits is NONCE_OVERFLOW");
        yield return new TestCaseData(FrameTxPayload.Encode(maxPriorityFeePerGas: ZeroWithLeadingZero).ToHexString(true),
                "TransactionException.RLP_LEADING_ZEROS_PRIORITY_FEE")
            .SetName("A priority fee with a leading zero is RLP_LEADING_ZEROS_PRIORITY_FEE");
        // EIP-8250's seq_beyond_width: the fixtures label a nonce_seq wider than 64 bits TYPE_6_INVALID_FRAME_FORMAT only.
        yield return new TestCaseData(FrameTxPayload.Encode(nonceKeys: [Rlp.Encode(1L)], nonce: NonceOf2Pow64).ToHexString(true),
                "TransactionException.TYPE_6_INVALID_FRAME_FORMAT")
            .SetName("A keyed nonce_seq wider than 64 bits is TYPE_6_INVALID_FRAME_FORMAT");
        yield return new TestCaseData(FrameTxPayload.Encode(fees: Rlp.Encode(new[] { Rlp.Encode(0L) })).ToHexString(true), // the list [max_fee]
                "TransactionException.TYPE_6_INVALID_FRAME_FORMAT")
            .SetName("A fee list missing an element is TYPE_6_INVALID_FRAME_FORMAT");
    }

    [TestCaseSource(nameof(LabelledRejectionCases))]
    public void RejectionMatchesItsLabel(string txBytes, string expectedException)
    {
        Result result = RunTest(TestFor(txBytes, expectedException));

        Assert.That((bool)result, Is.True, result.Error);
    }

    private static IEnumerable<TestCaseData> MislabelledRejectionCases()
    {
        yield return new TestCaseData(FrameTxPayload.Encode(nonce: NonceOf2Pow64).ToHexString(true),
                "TransactionException.NONCE_TOO_BIG")
            .SetName("A nonce wider than 64 bits is not NONCE_TOO_BIG");
        yield return new TestCaseData("0x06", "TransactionException.TYPE_6_INVALID_SIGNATURE")
            .SetName("The type byte alone is not TYPE_6_INVALID_SIGNATURE");
    }

    [TestCaseSource(nameof(MislabelledRejectionCases))]
    public void RejectionDoesNotMatchAnotherLabel(string txBytes, string expectedException)
    {
        Result result = RunTest(TestFor(txBytes, expectedException));

        Assert.That((bool)result, Is.False);
    }

    /// <summary>The truncation labels take only a decoder's explicit truncation report.</summary>
    /// <remarks>The EIP-1559 decoder has no end-of-input check, so <c>0x02c0</c> reads past the end of the input and
    /// <c>RlpDecoder</c> reports the index exception as <c>Cannot decode stream of Transaction</c>, the same text a
    /// decoder indexing bug produces.</remarks>
    [Test]
    public void ReadPastTheEndOfTheInputIsNotTakenAsTruncation()
    {
        const string labels = "TransactionException.RLP_ERROR_EOF|TransactionException.RLP_TOO_FEW_ELEMENTS";

        Result result = RunTest(TestFor("0x02c0", labels));

        Assert.That(result.Error, Is.EqualTo($"Expected '{labels}' but got 'Cannot decode stream of Transaction'."));
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
