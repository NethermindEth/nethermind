// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class Eip8282ConstantsTests
{
    [Test]
    public void Builder_predeploy_addresses_match_glamsterdam_devnet7()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8282Constants.BuilderDepositRequestPredeployAddress, Is.EqualTo(new Address("0x0000BFF46984E3725691FA540A8C7589300D8282")));
            Assert.That(Eip8282Constants.BuilderExitRequestPredeployAddress, Is.EqualTo(new Address("0x000064D678505AD48F8CCB093BC65613800E8282")));
        }
    }

    [Test]
    [TestCaseSource(nameof(BuilderBytecodeCases))]
    public void Builder_bytecode_hash_matches_glamsterdam_devnet7(byte[] code, ValueHash256 codeHash, int length, string expectedHash)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Has.Length.EqualTo(length));
            Assert.That(codeHash, Is.EqualTo(new ValueHash256(expectedHash)));
        }
    }

    private static IEnumerable<TestCaseData> BuilderBytecodeCases()
    {
        yield return new TestCaseData(
            Eip8282TestConstants.BuilderDeposit.Code,
            Eip8282TestConstants.BuilderDeposit.CodeHash,
            628,
            "0x1dd29c1e0dbc3ab670d229dbd3438003ec9015c1df9058beeb64ff301b60b98d")
            .SetName("BuilderDeposit");

        yield return new TestCaseData(
            Eip8282TestConstants.BuilderExit.Code,
            Eip8282TestConstants.BuilderExit.CodeHash,
            458,
            "0x90a0b24eb190d6c50f00f6f751dc4c2778658abf3631aceb80586c43f8bd9f2f")
            .SetName("BuilderExit");
    }
}
