// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        var result = new ArrayPoolList<byte[]>(MaxRequestsCount);

        if (depositRequests.Length > 0)
        {
            result.Add(FlatEncodeRequests(depositRequests, depositRequests.Length * DepositRequestsBytesSize, (byte)ExecutionRequestType.Deposit));
        }

        if (withdrawalRequests.Length > 0)
        {
            result.Add(FlatEncodeRequests(withdrawalRequests, withdrawalRequests.Length * WithdrawalRequestsBytesSize, (byte)ExecutionRequestType.WithdrawalRequest));
        }

        if (consolidationRequests.Length > 0)
        {
            result.Add(FlatEncodeRequests(consolidationRequests, consolidationRequests.Length * ConsolidationRequestsBytesSize, (byte)ExecutionRequestType.ConsolidationRequest));
        }

        return result;

        static byte[] FlatEncodeRequests(ExecutionRequest[] requests, int bufferSize, byte type)
        {
            using ArrayPoolList<byte> buffer = new(bufferSize + 1) { type };

            foreach (ExecutionRequest request in requests)
            {
                buffer.AddRange(request.RequestData!);
            }

            return buffer.ToArray();
        }
    }
}
