// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Test.Builders;

public class TestExecutionRequest : ExecutionRequest.ExecutionRequest
{
    private byte[][]? _requestDataParts;

    public byte[][]? RequestDataParts
    {
        get => _requestDataParts;
        set
        {
            _requestDataParts = value;
            RequestData = value is null ? null : Bytes.Concat(value.AsSpan());
        }
    }
}

public static class TestExecutionRequestExtensions
{
    public static ArrayPoolList<byte[]> GetFlatEncodedRequests(
        TestExecutionRequest[] depositRequests,
        TestExecutionRequest[] withdrawalRequests,
        TestExecutionRequest[] consolidationRequests
    )
    {
        return new(ExecutionRequestExtensions.RequestPartsCount)
        {
            FlatEncodeRequests(depositRequests, depositRequests.Length * ExecutionRequestExtensions.DepositRequestsBytesSize),
            FlatEncodeRequests(withdrawalRequests, withdrawalRequests.Length * ExecutionRequestExtensions.WithdrawalRequestsBytesSize),
            FlatEncodeRequests(consolidationRequests, consolidationRequests.Length * ExecutionRequestExtensions.ConsolidationRequestsBytesSize)
        };

        static byte[] FlatEncodeRequests(TestExecutionRequest[] requests, int bufferSize)
        {
            using ArrayPoolList<byte> buffer = new(bufferSize);

            foreach (TestExecutionRequest request in requests)
            {
                buffer.AddRange(request.RequestData!);
            }

            return buffer.ToArray();
        }
    }
}
