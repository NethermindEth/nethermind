// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.ExecutionRequest;

public enum ExecutionRequestType : byte
{
    Deposit = 0,
    WithdrawalRequest = 1,
    ConsolidationRequest = 2
}

public class ExecutionRequest
{
    public byte RequestType { get; set; }
    public byte[]? RequestData { get; set; }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(ExecutionRequest)}
            {{{nameof(RequestType)}: {RequestType},
            {nameof(RequestData)}: {RequestData!.ToHexString()}}}";
}

public static class ExecutionRequestExtensions
{
    public const int depositRequestsBytesSize = 48 + 32 + 8 + 96 + 8;
    public const int withdrawalRequestsBytesSize = 20 + 48 + 8;
    public const int consolidationRequestsBytesSize = 20 + 48 + 48;

    public static int GetRequestsByteSize(this IEnumerable<ExecutionRequest> requests)
    {
        int size = 0;
        foreach (ExecutionRequest request in requests)
        {
            size += request.RequestData!.Length + 1;
        }
        return size;
    }

    public static void FlatEncodeWithoutType(this IEnumerable<ExecutionRequest> requests, Span<byte> buffer)
    {
        int currentPosition = 0;

        foreach (ExecutionRequest request in requests)
        {
            // Ensure the buffer has enough space to accommodate the new data
            if (currentPosition + request.RequestData!.Length > buffer.Length)
            {
                throw new InvalidOperationException("Buffer is not large enough to hold all data of requests");
            }

            // Copy the RequestData to the buffer at the current position
            request.RequestData.CopyTo(buffer.Slice(currentPosition, request.RequestData.Length));
            currentPosition += request.RequestData.Length;
        }
    }

    public static ArrayPoolList<byte[]> GetFlatEncodedRequests(
        IEnumerable<ExecutionRequest> depositRequests,
        IEnumerable<ExecutionRequest> withdrawalRequests,
        IEnumerable<ExecutionRequest> consolidationRequests
    )
    {
        ArrayPoolList<byte[]> requests = new(3);
        using ArrayPoolList<byte> depositBuffer = new (depositRequestsBytesSize);
        using ArrayPoolList<byte> withdrawalBuffer = new (withdrawalRequestsBytesSize);
        using ArrayPoolList<byte> consolidationBuffer = new (consolidationRequestsBytesSize);

        
        foreach (ExecutionRequest request in depositRequests)
        {
            depositBuffer.AddRange(request.RequestData!);
        }

        foreach (ExecutionRequest request in withdrawalRequests)
        {
            withdrawalBuffer.AddRange(request.RequestData!);
        }

        foreach (ExecutionRequest request in consolidationRequests)
        {
            consolidationBuffer.AddRange(request.RequestData!);
        }

        requests.AddRange(depositBuffer.ToArray());
        requests.AddRange(withdrawalBuffer.ToArray());
        requests.AddRange(consolidationBuffer.ToArray());
        return requests;
    }

    public static Hash256 CalculateHashFromFlatEncodedRequests(byte[][]? flatEncodedRequests)
    {
        // make sure that length is exactly 3
        if (flatEncodedRequests is null || flatEncodedRequests.Length != 3)
        {
            throw new ArgumentException("Flat encoded requests must be an array of 3 elements");
        }

        using (SHA256 sha256 = SHA256.Create())
        {
            Span<byte> concatenatedHashes = new byte[32 * flatEncodedRequests!.Length];
            int currentPosition = 0;
            byte type = 0;
            // Compute sha256 for each request and concatenate them
            foreach (byte[] request in flatEncodedRequests)
            {
                if (type > 2) break;
                Span<byte> requestBuffer = new byte[request.Length + 1];
                requestBuffer[0] = type;
                request.CopyTo(requestBuffer.Slice(1));
                sha256.ComputeHash(requestBuffer.ToArray()).CopyTo(concatenatedHashes.Slice(currentPosition, 32));
                currentPosition += 32;
                type++;
            }

            // Compute sha256 of the concatenated hashes
            return new Hash256(sha256.ComputeHash(concatenatedHashes.ToArray()));
        }
    }

    public static Hash256 CalculateHash(
        IEnumerable<ExecutionRequest> depositRequests,
        IEnumerable<ExecutionRequest> withdrawalRequests,
        IEnumerable<ExecutionRequest> consolidationRequests
    )
    {
        using ArrayPoolList<byte[]> requests = GetFlatEncodedRequests(depositRequests, withdrawalRequests, consolidationRequests);
        return CalculateHashFromFlatEncodedRequests(requests.ToArray());
    }

    public static bool IsSortedByType(this ExecutionRequest[] requests)
    {
        for (int i = 1; i < requests.Length; i++)
        {
            if (requests[i - 1].RequestType > requests[i].RequestType)
            {
                return false;
            }
        }
        return true;
    }
}
