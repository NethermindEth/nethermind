// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class VotesManagerTests
{
    [Test]
    public void CountValidSignatures_UnrecoverableSignature_IsReportedNotThrown()
    {
        ValueHash256 message = TestItem.KeccakA.ValueHash256;
        EthereumEcdsa ecdsa = new(0);
        Signature valid = ecdsa.Sign(TestItem.PrivateKeyA, message);
        // r = 0 is never a point on the curve, yet it has a low s, so it reaches address recovery.
        Signature unrecoverable = new(new byte[32], valid.SAsSpan, valid.V);

        int? count = VotesManager.CountValidSignatures([TestItem.AddressA], [valid, unrecoverable], message, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.Null);
            Assert.That(error, Is.EqualTo("Certificate contains an invalid signature"));
        }
    }
}
