// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Int256;
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
    public void Function_matches_pinned_abi<TArgs, TResult>(ContractFunction<TArgs, TResult> function, string signature, string selector, string returns) =>
        AbiAssert.FunctionMatches(function, signature, selector, returns);

    [Test]
    public void ExecuteSystemWithdrawals_calldata_matches_golden_encoding()
    {
        byte[] data = AbiCodec.Encode(WithdrawalContract.ExecuteSystemWithdrawalsFunction.Signature,
            ((UInt256)4, [1ul, 2ul], [new Address("0x1111111111111111111111111111111111111111"), new Address("0x2222222222222222222222222222222222222222")]));

        Assert.That(data.ToHexString(true), Is.EqualTo(
            "0x79d0c0bc" +
            "0000000000000000000000000000000000000000000000000000000000000004" +
            "0000000000000000000000000000000000000000000000000000000000000060" +
            "00000000000000000000000000000000000000000000000000000000000000c0" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000001" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000001111111111111111111111111111111111111111" +
            "0000000000000000000000002222222222222222222222222222222222222222"));
    }
}
