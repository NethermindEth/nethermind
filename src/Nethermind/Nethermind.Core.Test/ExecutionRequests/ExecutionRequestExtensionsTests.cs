// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using CoreExecutionRequest = Nethermind.Core.ExecutionRequest.ExecutionRequest;

namespace Nethermind.Core.Test.ExecutionRequests;

public class ExecutionRequestExtensionsTests
{
    private static readonly TestExecutionRequest[] DepositRequests = [TestItem.ExecutionRequestA, TestItem.ExecutionRequestB, TestItem.ExecutionRequestC];
    private static readonly TestExecutionRequest[] WithdrawalRequests = [TestItem.ExecutionRequestD, TestItem.ExecutionRequestE, TestItem.ExecutionRequestF];
    private static readonly TestExecutionRequest[] ConsolidationRequests = [TestItem.ExecutionRequestG, TestItem.ExecutionRequestH, TestItem.ExecutionRequestI];
    private static readonly TestExecutionRequest[] BuilderDepositRequests = [TestItem.ExecutionRequestJ, TestItem.ExecutionRequestK];
    private static readonly TestExecutionRequest[] BuilderExitRequests = [TestItem.ExecutionRequestL, TestItem.ExecutionRequestM];

    [TestCaseSource(nameof(RequestHashCases))]
    public void CalculateHashFromFlatEncodedRequests_KnownGroups_MatchesExpectedHash(byte[][] requests, string expectedHash)
    {
        Hash256 hash = ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(requests);

        Assert.That(hash, Is.EqualTo(new Hash256(expectedHash)));
    }

    [Test]
    public void GetFlatEncodedRequests_encodes_request_groups()
    {
        using ArrayPoolList<byte[]> flatEncodedRequests = ExecutionRequestExtensions.GetFlatEncodedRequests(
            DepositRequests,
            WithdrawalRequests,
            ConsolidationRequests,
            BuilderDepositRequests,
            BuilderExitRequests);

        Assert.That(flatEncodedRequests, Has.Count.EqualTo(ExecutionRequestExtensions.MaxRequestsCount));
        AssertFlatEncodedRequests(flatEncodedRequests[0], ExecutionRequestType.Deposit, DepositRequests, ExecutionRequestExtensions.DepositRequestsBytesSize);
        AssertFlatEncodedRequests(flatEncodedRequests[1], ExecutionRequestType.WithdrawalRequest, WithdrawalRequests, ExecutionRequestExtensions.WithdrawalRequestsBytesSize);
        AssertFlatEncodedRequests(flatEncodedRequests[2], ExecutionRequestType.ConsolidationRequest, ConsolidationRequests, ExecutionRequestExtensions.ConsolidationRequestsBytesSize);
        AssertFlatEncodedRequests(flatEncodedRequests[3], ExecutionRequestType.BuilderDepositRequest, BuilderDepositRequests, ExecutionRequestExtensions.BuilderDepositRequestsBytesSize);
        AssertFlatEncodedRequests(flatEncodedRequests[4], ExecutionRequestType.BuilderExitRequest, BuilderExitRequests, ExecutionRequestExtensions.BuilderExitRequestsBytesSize);
    }

    [Test]
    public void GetFlatEncodedRequests_preserves_legacy_overload()
    {
        using ArrayPoolList<byte[]> flatEncodedRequests = ExecutionRequestExtensions.GetFlatEncodedRequests(
            DepositRequests,
            WithdrawalRequests,
            ConsolidationRequests);

        Assert.That(flatEncodedRequests, Has.Count.EqualTo(ExecutionRequestExtensions.StatelessRequestTypesCount));
        AssertFlatEncodedRequests(flatEncodedRequests[0], ExecutionRequestType.Deposit, DepositRequests, ExecutionRequestExtensions.DepositRequestsBytesSize);
        AssertFlatEncodedRequests(flatEncodedRequests[1], ExecutionRequestType.WithdrawalRequest, WithdrawalRequests, ExecutionRequestExtensions.WithdrawalRequestsBytesSize);
        AssertFlatEncodedRequests(flatEncodedRequests[2], ExecutionRequestType.ConsolidationRequest, ConsolidationRequests, ExecutionRequestExtensions.ConsolidationRequestsBytesSize);
    }

    [Test]
    public void GetFlatEncodedRequests_skips_empty_request_groups()
    {
        TestExecutionRequest[] withdrawalRequests = [TestItem.ExecutionRequestD];

        using ArrayPoolList<byte[]> flatEncodedRequests = ExecutionRequestExtensions.GetFlatEncodedRequests(
            [],
            withdrawalRequests,
            [],
            [],
            []);

        Assert.That(flatEncodedRequests, Has.Count.EqualTo(1));
        AssertFlatEncodedRequests(flatEncodedRequests[0], ExecutionRequestType.WithdrawalRequest, withdrawalRequests, ExecutionRequestExtensions.WithdrawalRequestsBytesSize);
    }

    [Test]
    public void GetAllFlatDecodedRequests_decodes_flat_encoded_requests()
    {
        using ArrayPoolList<byte[]> flatEncodedRequests = ExecutionRequestExtensions.GetFlatEncodedRequests(
            DepositRequests,
            WithdrawalRequests,
            ConsolidationRequests,
            BuilderDepositRequests,
            BuilderExitRequests);
        (CoreExecutionRequest[] decodedDepositRequests,
            CoreExecutionRequest[] decodedWithdrawalRequests,
            CoreExecutionRequest[] decodedConsolidationRequests,
            CoreExecutionRequest[] decodedBuilderDepositRequests,
            CoreExecutionRequest[] decodedBuilderExitRequests) =
            ExecutionRequestExtensions.GetAllFlatDecodedRequests([.. flatEncodedRequests]);

        AssertRequests(DepositRequests, decodedDepositRequests);
        AssertRequests(WithdrawalRequests, decodedWithdrawalRequests);
        AssertRequests(ConsolidationRequests, decodedConsolidationRequests);
        AssertRequests(BuilderDepositRequests, decodedBuilderDepositRequests);
        AssertRequests(BuilderExitRequests, decodedBuilderExitRequests);
    }

    [Test]
    public void GetFlatDecodedRequests_preserves_legacy_contract()
    {
        using ArrayPoolList<byte[]> flatEncodedRequests = ExecutionRequestExtensions.GetFlatEncodedRequests(
            DepositRequests,
            WithdrawalRequests,
            ConsolidationRequests);
        (CoreExecutionRequest[] decodedDepositRequests,
            CoreExecutionRequest[] decodedWithdrawalRequests,
            CoreExecutionRequest[] decodedConsolidationRequests) =
            ExecutionRequestExtensions.GetFlatDecodedRequests([.. flatEncodedRequests]);

        AssertRequests(DepositRequests, decodedDepositRequests);
        AssertRequests(WithdrawalRequests, decodedWithdrawalRequests);
        AssertRequests(ConsolidationRequests, decodedConsolidationRequests);
    }

    [Test]
    public void GetFlatDecodedRequests_rejects_builder_requests()
    {
        using ArrayPoolList<byte[]> flatEncodedRequests = ExecutionRequestExtensions.GetFlatEncodedRequests(
            [],
            [],
            [],
            BuilderDepositRequests,
            []);

        Assert.That(
            () => ExecutionRequestExtensions.GetFlatDecodedRequests([.. flatEncodedRequests]),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void GetAllFlatDecodedRequests_allows_empty_request_groups()
    {
        (CoreExecutionRequest[] depositRequests,
            CoreExecutionRequest[] withdrawalRequests,
            CoreExecutionRequest[] consolidationRequests,
            CoreExecutionRequest[] builderDepositRequests,
            CoreExecutionRequest[] builderExitRequests) =
            ExecutionRequestExtensions.GetAllFlatDecodedRequests([[(byte)ExecutionRequestType.Deposit]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(depositRequests, Is.Empty);
            Assert.That(withdrawalRequests, Is.Empty);
            Assert.That(consolidationRequests, Is.Empty);
            Assert.That(builderDepositRequests, Is.Empty);
            Assert.That(builderExitRequests, Is.Empty);
        }
    }

    [Test]
    public void GetAllFlatDecodedRequests_rejects_empty_request_blob() =>
        Assert.Throws<ArgumentException>(() => ExecutionRequestExtensions.GetAllFlatDecodedRequests([[]]));

    [TestCase((byte)ExecutionRequestType.Deposit, ExecutionRequestExtensions.DepositRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.WithdrawalRequest, ExecutionRequestExtensions.WithdrawalRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.ConsolidationRequest, ExecutionRequestExtensions.ConsolidationRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.BuilderDepositRequest, ExecutionRequestExtensions.BuilderDepositRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.BuilderExitRequest, ExecutionRequestExtensions.BuilderExitRequestsBytesSize)]
    public void GetAllFlatDecodedRequests_rejects_partial_request_data(byte type, int requestDataSize)
    {
        byte[] encodedRequests = new byte[requestDataSize];
        encodedRequests[0] = type;

        Assert.Throws<ArgumentException>(() =>
        {
            ExecutionRequestExtensions.GetAllFlatDecodedRequests([encodedRequests]);
        });
    }

    [Test]
    public void GetAllFlatDecodedRequests_rejects_unknown_request_type() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ExecutionRequestExtensions.GetAllFlatDecodedRequests([[byte.MaxValue, 0]]));

    [Test]
    public void GetAllFlatDecodedRequests_rejects_duplicate_request_type() =>
        Assert.Throws<ArgumentException>(() =>
            ExecutionRequestExtensions.GetAllFlatDecodedRequests([
                [(byte)ExecutionRequestType.Deposit],
                [(byte)ExecutionRequestType.Deposit]
            ]));

    [Test]
    public void GetAllFlatDecodedRequests_rejects_descending_request_type() =>
        Assert.Throws<ArgumentException>(() =>
            ExecutionRequestExtensions.GetAllFlatDecodedRequests([
                [(byte)ExecutionRequestType.WithdrawalRequest],
                [(byte)ExecutionRequestType.Deposit]
            ]));

    private static IEnumerable<TestCaseData> RequestHashCases()
    {
        yield return new TestCaseData(Array.Empty<byte[]>(), "0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
            .SetName("RequestHash_EmptyGroups");
        yield return new TestCaseData(new byte[][] { [], [0] }, "0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
            .SetName("RequestHash_EmptyAndTypeOnlyGroups");
        yield return new TestCaseData(new byte[][] { [0, 1, 2] }, "0x35f9f2930ea344618822795eeec568ae67ab2987d8b19e4591e10527baa1df3d")
            .SetName("RequestHash_SingleGroup");
        yield return new TestCaseData(new byte[][] { [], [0], [0, 1, 2], [1, 3, 4] }, "0xa85c90722e89ff03f65367c77c8f5d562fddd99f55303a009305071edc8b2d09")
            .SetName("RequestHash_MixedGroups");
        yield return new TestCaseData(new byte[][] { [0, 1], [1, 2], [2, 3], [3, 4], [4, 5] }, "0x90f4aa3f9e3a22439310d1d798a1417d1bc2aaf662e037b2c139f2fcc21d690d")
            .SetName("RequestHash_FiveGroups");
        yield return new TestCaseData(new byte[][] { [0, 1], [1, 2], [2, 3], [3, 4], [4, 5], [5, 6] }, "0xed503c0637d05601c0a74421d841445a90a28d142f1e959b4b3df0f8a8dd81f8")
            .SetName("RequestHash_MoreGroupsThanInitialCapacity");
    }

    private static void AssertFlatEncodedRequests(byte[] encodedRequests, ExecutionRequestType expectedType, CoreExecutionRequest[] expectedRequests, int requestDataSize)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encodedRequests[0], Is.EqualTo((byte)expectedType));
            Assert.That(encodedRequests, Has.Length.EqualTo(1 + expectedRequests.Length * requestDataSize));
        }

        int offset = 1;
        for (int i = 0; i < expectedRequests.Length; i++)
        {
            byte[] requestData = expectedRequests[i].RequestData!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(requestData, Has.Length.EqualTo(requestDataSize));
                Assert.That(encodedRequests.AsSpan(offset, requestData.Length).SequenceEqual(requestData), Is.True);
            }
            offset += requestData.Length;
        }
    }

    private static void AssertRequests(CoreExecutionRequest[] expectedRequests, CoreExecutionRequest[] actualRequests)
    {
        Assert.That(actualRequests, Has.Length.EqualTo(expectedRequests.Length));

        for (int i = 0; i < expectedRequests.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actualRequests[i].RequestType, Is.EqualTo(expectedRequests[i].RequestType));
                Assert.That(actualRequests[i].RequestData, Is.EqualTo(expectedRequests[i].RequestData));
            }
        }
    }
}
