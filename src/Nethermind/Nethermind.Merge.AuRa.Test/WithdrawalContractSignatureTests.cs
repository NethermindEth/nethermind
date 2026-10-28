// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core.Extensions;
using Nethermind.Merge.AuRa.Contracts;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class WithdrawalContractSignatureTests
{
    private static IEnumerable<TestCaseData> Functions()
    {
        yield return new TestCaseData(WithdrawalContract.ExecuteSystemWithdrawalsFunction, "executeSystemWithdrawals(uint256,uint64[],address[])", "0x79d0c0bc", "()");
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
