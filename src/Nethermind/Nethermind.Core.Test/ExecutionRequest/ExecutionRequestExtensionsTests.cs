// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using CoreExecutionRequest = Nethermind.Core.ExecutionRequest.ExecutionRequest;

namespace Nethermind.Core.Test.ExecutionRequests;

[TestFixture]
public class ExecutionRequestExtensionsTests
{
    [Test]
    public void GetFlatDecodedRequests_decodes_flat_encoded_requests()
    {
        TestExecutionRequest[] depositRequests = [TestItem.ExecutionRequestA, TestItem.ExecutionRequestB, TestItem.ExecutionRequestC];
        TestExecutionRequest[] withdrawalRequests = [TestItem.ExecutionRequestD, TestItem.ExecutionRequestE, TestItem.ExecutionRequestF];
        TestExecutionRequest[] consolidationRequests = [TestItem.ExecutionRequestG, TestItem.ExecutionRequestH, TestItem.ExecutionRequestI];

        using ArrayPoolList<byte[]> flatEncodedRequests = ExecutionRequestExtensions.GetFlatEncodedRequests(depositRequests, withdrawalRequests, consolidationRequests);
        (CoreExecutionRequest[] decodedDepositRequests,
            CoreExecutionRequest[] decodedWithdrawalRequests,
            CoreExecutionRequest[] decodedConsolidationRequests) =
            ExecutionRequestExtensions.GetFlatDecodedRequests(flatEncodedRequests.AsSpan().ToArray());

        AssertRequests(depositRequests, decodedDepositRequests);
        AssertRequests(withdrawalRequests, decodedWithdrawalRequests);
        AssertRequests(consolidationRequests, decodedConsolidationRequests);
    }

    [Test]
    public void GetFlatDecodedRequests_allows_empty_request_groups()
    {
        (CoreExecutionRequest[] depositRequests,
            CoreExecutionRequest[] withdrawalRequests,
            CoreExecutionRequest[] consolidationRequests) =
            ExecutionRequestExtensions.GetFlatDecodedRequests([[(byte)ExecutionRequestType.Deposit]]);

        Assert.That(depositRequests, Is.Empty);
        Assert.That(withdrawalRequests, Is.Empty);
        Assert.That(consolidationRequests, Is.Empty);
    }

    [Test]
    public void GetFlatDecodedRequests_rejects_empty_request_blob() =>
        Assert.Throws<ArgumentException>(() => ExecutionRequestExtensions.GetFlatDecodedRequests([[]]));

    [TestCase((byte)ExecutionRequestType.Deposit, ExecutionRequestExtensions.DepositRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.WithdrawalRequest, ExecutionRequestExtensions.WithdrawalRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.ConsolidationRequest, ExecutionRequestExtensions.ConsolidationRequestsBytesSize)]
    public void GetFlatDecodedRequests_rejects_partial_request_data(byte type, int requestDataSize)
    {
        byte[] encodedRequests = new byte[requestDataSize];
        encodedRequests[0] = type;

        Assert.Throws<ArgumentException>(() =>
        {
            ExecutionRequestExtensions.GetFlatDecodedRequests([encodedRequests]);
        });
    }

    [Test]
    public void GetFlatDecodedRequests_rejects_unknown_request_type() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ExecutionRequestExtensions.GetFlatDecodedRequests([[byte.MaxValue, 0]]));

    [Test]
    public void GetFlatDecodedRequests_rejects_duplicate_request_type() =>
        Assert.Throws<ArgumentException>(() =>
            ExecutionRequestExtensions.GetFlatDecodedRequests([
                [(byte)ExecutionRequestType.Deposit],
                [(byte)ExecutionRequestType.Deposit]
            ]));

    [Test]
    public void GetFlatDecodedRequests_rejects_descending_request_type() =>
        Assert.Throws<ArgumentException>(() =>
            ExecutionRequestExtensions.GetFlatDecodedRequests([
                [(byte)ExecutionRequestType.WithdrawalRequest],
                [(byte)ExecutionRequestType.Deposit]
            ]));

    private static void AssertRequests(CoreExecutionRequest[] expectedRequests, CoreExecutionRequest[] actualRequests)
    {
        Assert.That(actualRequests.Length, Is.EqualTo(expectedRequests.Length));

        for (int i = 0; i < expectedRequests.Length; i++)
        {
            Assert.That(actualRequests[i].RequestType, Is.EqualTo(expectedRequests[i].RequestType));
            Assert.That(actualRequests[i].RequestData, Is.EqualTo(expectedRequests[i].RequestData));
        }
    }
}
