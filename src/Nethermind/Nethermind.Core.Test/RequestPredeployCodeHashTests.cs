// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class RequestPredeployCodeHashTests
{
    // The BAL surplus-reads budget trusts these hashes to identify code that reads only its own storage.
    [TestCaseSource(nameof(Cases))]
    public void Production_code_hash_matches_canonical_bytecode(ValueHash256 productionHash, byte[] canonicalCode) =>
        Assert.That(productionHash, Is.EqualTo(ValueKeccak.Compute(canonicalCode)));

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return new TestCaseData(Eip7002Constants.CodeHash, Eip7002TestConstants.Code).SetName("WithdrawalRequest");
        yield return new TestCaseData(Eip7251Constants.CodeHash, Eip7251TestConstants.Code).SetName("ConsolidationRequest");
        yield return new TestCaseData(Eip8282Constants.BuilderDepositCodeHash, Eip8282TestConstants.BuilderDeposit.Code).SetName("BuilderDeposit");
        yield return new TestCaseData(Eip8282Constants.BuilderExitCodeHash, Eip8282TestConstants.BuilderExit.Code).SetName("BuilderExit");
    }
}
