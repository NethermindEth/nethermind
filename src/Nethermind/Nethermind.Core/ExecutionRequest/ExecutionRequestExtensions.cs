// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.ExecutionRequest;

public static class ExecutionRequestExtensions
{

    public const int DepositRequestsBytesSize = 48 /*pubkey: Bytes48 */ + 32 /*withdrawal_credentials: Bytes32 */+ 8 /*amount: uint64*/ + 96 /*signature: Bytes96*/+ 8 /*index: uint64*/;
    public const int WithdrawalRequestsBytesSize = Address.Size + 48 /*validator_pubkey: Bytes48*/ + 8 /*amount: uint64*/;
    public const int ConsolidationRequestsBytesSize = Address.Size + 48 /*source_pubkey: Bytes48*/ + 48 /*target_pubkey: Bytes48*/;

    public static byte[][] EmptyRequests = new byte[][] { Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>() };
    public static Hash256 EmptyRequestsHash = CalculateHashFromFlatEncodedRequests(EmptyRequests);

    public static int GetRequestsByteSize(this IEnumerable<ExecutionRequest> requests)
    {
        int size = 0;
        foreach (ExecutionRequest request in requests)
        {
            size += request.RequestData!.Length + 1;
        }
        return size;
    }

    [SkipLocalsInit]
    public static Hash256 CalculateHashFromFlatEncodedRequests(byte[][]? flatEncodedRequests)
    {
        // make sure that length is exactly 3
        if (flatEncodedRequests is null || flatEncodedRequests.Length != 3)
        {
            throw new ArgumentException("Flat encoded requests must be an array of 3 elements");
        }

        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] concatenatedHashes = new byte[Hash256.Size * flatEncodedRequests!.Length];
            int currentPosition = 0;
            byte type = 0;
            // Allocate the buffer once outside the loop
            Span<byte> requestBuffer = stackalloc byte[flatEncodedRequests.Max(r => r.Length) + 1];
            // Compute sha256 for each request and concatenate them
            foreach (byte[] requests in flatEncodedRequests)
            {
                requestBuffer[0] = type;
                requests.CopyTo(requestBuffer.Slice(1, requests.Length));
                sha256.ComputeHash(requestBuffer.Slice(0, requests.Length + 1).ToArray()).CopyTo(concatenatedHashes.AsSpan(currentPosition, 32));
                currentPosition += 32;
                type++;
            }

            // Compute sha256 of the concatenated hashes
            return new Hash256(sha256.ComputeHash(concatenatedHashes.ToArray()));
        }
    }


    // the following functions are only used in tests
    public static ArrayPoolList<byte[]> GetFlatEncodedRequests(
        ExecutionRequest[] depositRequests,
        ExecutionRequest[] withdrawalRequests,
        ExecutionRequest[] consolidationRequests
    )
    {
        ArrayPoolList<byte[]> requests = new(3)
        {
            FlatEncodeRequests(depositRequests, depositRequests.Length * DepositRequestsBytesSize),
            FlatEncodeRequests(withdrawalRequests, withdrawalRequests.Length * WithdrawalRequestsBytesSize),
            FlatEncodeRequests(consolidationRequests, consolidationRequests.Length * ConsolidationRequestsBytesSize)
        };

        return requests;
    }

    public static byte[] FlatEncodeRequests(ExecutionRequest[] requests, int bufferSize)
    {
        using ArrayPoolList<byte> buffer = new(bufferSize);

        foreach (ExecutionRequest request in requests)
        {
            buffer.AddRange(request.RequestData!);
        }
        return buffer.ToArray();
    }
}
