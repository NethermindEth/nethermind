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
/// Guards the client wordings behind each frame-transaction fixture label: that the decoder's real
/// rejection messages are covered, and that the sets stay pairwise disjoint.
/// </summary>
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
        // "Unexpected RLP prefix" is the address-decode wording; a field that should be a sequence
        // and is not reports the accepted range instead.
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

    // The detailed wording is composed only when Rlp's static logger has trace enabled, so a set
    // holding it alone leaves the mapping dead in the default configuration.
    [TestCase("An RLP limit exceeded")]
    [TestCase("Collection count of 33 is over limit 32 or 40 bytes left")]
    public void FeeOverflow_CoversBothWordingsOfTheLengthGuard(string message) =>
        Assert.That(Covers(FrameExceptionFragments.FeeOverflow, message), Is.True);

    // Both entry points, because the processor words a halt differently under the mempool admission
    // simulator, whose wordings no fixture reaches, than on the block-processing path.
    [TestCase("VERIFY frame reverted")]
    [TestCase("validation prefix frame reverted")]
    [TestCase("SENDER frame before execution approval")]
    [TestCase("frame transaction never set a payer")]
    [TestCase("frame transaction validation prefix never set a payer")]
    public void Execution_CoversTheHaltWordingsOfBothEntryPoints(string message) =>
        Assert.That(Covers(FrameExceptionFragments.Execution, message), Is.True);

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
