// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Ethereum.Transaction.Test;

/// <summary>
/// Guards the client wordings behind each frame-transaction fixture label.
/// </summary>
/// <remarks>
/// The two <c>DecodeFailureMessage</c> tests drive the real decoder, so they fail if its wording moves
/// away from the table. The rest assert the table against literals and cannot: a processor reword
/// leaves both the case and the fragment green while the mapping goes dead. Reaching the processor
/// wordings the same way needs constants behind them, which this fixture cannot add on its own.
/// </remarks>
public class FrameExceptionFragmentsTests
{
    private static bool Covers(IEnumerable<string> fragments, string message)
    {
        foreach (string fragment in fragments)
        {
            if (message.Contains(fragment, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Encodes a frame transaction payload, defective only in the field the caller varies.</summary>
    private static string DecodeFailureMessage(Rlp frames, Rlp maxPriorityFeePerGas)
    {
        Rlp payloadSequence = Rlp.Encode(
            Rlp.Encode(1L),                      // chain_id
            Rlp.Encode(0L),                      // nonce
            Rlp.Encode(TestItem.AddressA.Bytes), // sender
            frames,
            Rlp.Encode(Array.Empty<Rlp>()),      // signatures
            Rlp.Encode(maxPriorityFeePerGas, Rlp.Encode(0L), Rlp.Encode(0L)),
            Rlp.Encode(Array.Empty<Rlp>()));     // blob_versioned_hashes

        byte[] payload = new byte[1 + payloadSequence.Length];
        payload[0] = (byte)TxType.FrameTx;
        payloadSequence.Bytes.CopyTo(payload, 1);

        // Catch, not Throws: the length guard raises the RlpLimitException subclass.
        RlpException thrown = Assert.Catch<RlpException>(() =>
        {
            RlpReader reader = new(payload);
            Rlp.GetDecoder<Nethermind.Core.Transaction>()!
                .DecodeGuardNotNull(ref reader, RlpBehaviors.SkipTypedWrapping);
        })!;

        return thrown.Message;
    }

    [Test]
    public void Decode_CoversAFrameListThatIsNotASequence()
    {
        string message = DecodeFailureMessage(
            frames: Rlp.Encode(new byte[19]),               // a string where a list belongs
            maxPriorityFeePerGas: Rlp.Encode(0L));

        Assert.That(message, Does.Contain("Expected a sequence prefix"));
        Assert.That(Covers(FrameExceptionFragments.Decode, message), Is.True, message);
    }

    [Test]
    public void FeeOverflow_CoversAFeeFieldWiderThanItsType()
    {
        string message = DecodeFailureMessage(
            frames: Rlp.Encode(Array.Empty<Rlp>()),
            maxPriorityFeePerGas: Rlp.Encode(new byte[33])); // one byte over the 32-byte field

        Assert.That(Covers(FrameExceptionFragments.FeeOverflow, message), Is.True, message);
    }

    // Both wordings of one guard; see FrameExceptionFragments.FeeOverflow for which is which.
    [TestCase("An RLP limit exceeded")]
    [TestCase("Collection count of 33 is over limit 32 or 40 bytes left")]
    public void FeeOverflow_CoversBothWordingsOfTheLengthGuard(string message) =>
        Assert.That(Covers(FrameExceptionFragments.FeeOverflow, message), Is.True);

    // The validation-prefix wordings reach only the mempool admission simulator, so no fixture
    // produces them; they are pinned so a reword of that path does not slip past the set.
    [TestCase("VERIFY frame reverted")]
    [TestCase("validation prefix frame reverted")]
    [TestCase("SENDER frame before execution approval")]
    [TestCase("frame transaction never set a payer")]
    [TestCase("frame transaction validation prefix never set a payer")]
    public void Execution_CoversTheHaltWordingsOfBothEntryPoints(string message) =>
        Assert.That(Covers(FrameExceptionFragments.Execution, message), Is.True);

    [Test]
    public void DecodeCarriesEveryFeeOverflowWording()
    {
        // Decode inlines these rather than spreading FeeOverflow, which would make it read null if
        // ever declared below it. This is the check that keeps the two in step instead.
        foreach (string fragment in FrameExceptionFragments.FeeOverflow)
        {
            Assert.That(FrameExceptionFragments.Decode, Does.Contain(fragment));
        }
    }

    private static IEnumerable<TestCaseData> DisjointnessCases()
    {
        (string Name, string[] Set)[] sets =
        [
            ("Format", FrameExceptionFragments.Format),
            ("Signature", FrameExceptionFragments.Signature),
            ("Execution", FrameExceptionFragments.Execution),
        ];

        foreach ((string ownerName, string[] owner) in sets)
        {
            foreach ((string otherName, string[] other) in sets)
            {
                if (!ReferenceEquals(owner, other))
                {
                    yield return new TestCaseData(owner, other).SetName($"{ownerName} messages are not matched by {otherName}");
                }
            }
        }
    }

    [TestCaseSource(nameof(DisjointnessCases))]
    public void Sets_StayPairwiseDisjoint(string[] owner, string[] other)
    {
        // A fixture naming one label has to fail when the client rejects for one of the other two,
        // so no set may hold a fragment broad enough to catch another's messages.
        using (Assert.EnterMultipleScope())
        {
            foreach (string message in owner)
            {
                Assert.That(Covers(other, message), Is.False, message);
            }
        }
    }
}
