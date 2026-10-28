// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core.Test;
using Nethermind.Shutter.Contracts;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[Parallelizable(ParallelScope.All)]
class ShutterContractSignatureTests
{
    private static IEnumerable<TestCaseData> Functions()
    {
        yield return new TestCaseData(KeyBroadcastContract.GetEonKeyFunction, "getEonKey(uint64)", "0x8a0b8b28", "(bytes)");
        yield return new TestCaseData(KeyperSetContract.IsFinalizedFunction, "isFinalized()", "0x8d4e4083", "(bool)");
        yield return new TestCaseData(KeyperSetContract.GetThresholdFunction, "getThreshold()", "0xe75235b8", "(uint64)");
        yield return new TestCaseData(KeyperSetContract.GetMembersFunction, "getMembers()", "0x9eab5253", "(address[])");
        yield return new TestCaseData(KeyperSetManagerContract.GetKeyperSetAddressFunction, "getKeyperSetAddress(uint64)", "0xf90f3bed", "(address)");
        yield return new TestCaseData(KeyperSetManagerContract.GetKeyperSetIndexByBlockFunction, "getKeyperSetIndexByBlock(uint64)", "0x035cef15", "(uint64)");
        yield return new TestCaseData(ValidatorRegistryContract.GetNumUpdatesFunction, "getNumUpdates()", "0x00c7c019", "(uint256)");
        yield return new TestCaseData(ValidatorRegistryContract.GetUpdateFunction, "getUpdate(uint256)", "0x32cb25be", "((bytes,bytes))");
    }

    [TestCaseSource(nameof(Functions))]
    public void Function_matches_pinned_abi<TArgs, TResult>(ContractFunction<TArgs, TResult> function, string signature, string selector, string returns) =>
        AbiAssert.FunctionMatches(function, signature, selector, returns);

    [Test]
    public void TransactionSubmitted_matches_pinned_abi()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SequencerContract.TransactionSubmittedHash.ToString(), Is.EqualTo("0xa7f1b5467be46c45249fb93063cceef96c63ddad03819246bc7770e32d4f5b7d"));
            Assert.That(SequencerContract.TransactionSubmittedData.ToString(), Is.EqualTo("(uint64,uint64,bytes32,address,bytes,uint256)"));
        }
    }
}
