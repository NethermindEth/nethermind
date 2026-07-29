// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
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
