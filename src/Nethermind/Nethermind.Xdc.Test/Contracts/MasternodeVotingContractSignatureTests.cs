// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core.Extensions;
using Nethermind.Xdc.Contracts;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Contracts;

internal class MasternodeVotingContractSignatureTests
{
    private static IEnumerable<TestCaseData> Functions()
    {
        yield return new TestCaseData(MasternodeVotingContract.GetCandidatesFunction, "getCandidates()", "0x06a49fce", "(address[])");
        yield return new TestCaseData(MasternodeVotingContract.GetCandidateCapFunction, "getCandidateCap(address)", "0x58e7525f", "(uint256)");
        yield return new TestCaseData(MasternodeVotingContract.GetCandidateOwnerFunction, "getCandidateOwner(address)", "0xb642facd", "(address)");
    }

    [TestCaseSource(nameof(Functions))]
    public void Function_matches_pinned_abi<TArgs, TResult>(ContractFunction<TArgs, TResult> function, string signature, string selector, string returns)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(function.Signature.ToString(), Is.EqualTo(signature));
            Assert.That(function.Signature.MethodId.ToHexString(true), Is.EqualTo(selector));
            Assert.That(function.Returns.ToString(), Is.EqualTo(returns));
        }
    }
}
