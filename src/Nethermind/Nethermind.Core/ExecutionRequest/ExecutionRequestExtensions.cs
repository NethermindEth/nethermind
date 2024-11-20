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
    public const int DepositRequestsBytesSize = PublicKeySize /*pubkey: Bytes48 */ + Hash256.Size /*withdrawal_credentials: Bytes32 */ + sizeof(ulong) /*amount: uint64*/ + 96 /*signature: Bytes96*/ + sizeof(ulong) /*index: uint64*/;
    public const int WithdrawalRequestsBytesSize = Address.Size + PublicKeySize /*validator_pubkey: Bytes48*/ + sizeof(ulong) /*amount: uint64*/;
    public const int ConsolidationRequestsBytesSize = Address.Size + PublicKeySize /*source_pubkey: Bytes48*/ + PublicKeySize /*target_pubkey: Bytes48*/;
    public const int RequestPartsCount = 3;
    private const int PublicKeySize = 48;

    public static readonly byte[][] EmptyRequests = [[], [], []];
    public static readonly Hash256 EmptyRequestsHash = CalculateHashFromFlatEncodedRequests(EmptyRequests);

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
        if (flatEncodedRequests is null || flatEncodedRequests.Length != RequestPartsCount)
        {
            throw new ArgumentException("Flat encoded requests must be an array of 3 elements");
        }

        byte[] concatenatedHashes = new byte[Hash256.Size * RequestPartsCount];
        int currentPosition = 0;
        byte type = 0;
        // Allocate the buffer once outside the loop
        Span<byte> requestBuffer = stackalloc byte[Math.Max(Math.Max(flatEncodedRequests[0].Length, flatEncodedRequests[1].Length), flatEncodedRequests[2].Length) + 1];
        // Compute sha256 for each request and concatenate them
        foreach (byte[] requests in flatEncodedRequests)
        {
            requestBuffer[0] = type;
            requests.CopyTo(requestBuffer.Slice(1, requests.Length));
            SHA256.HashData(requestBuffer[..(requests.Length + 1)]).CopyTo(concatenatedHashes.AsSpan(currentPosition, Hash256.Size));
            currentPosition += Hash256.Size;
            type++;
        }

        // Compute sha256 of the concatenated hashes
        return new Hash256(SHA256.HashData(concatenatedHashes));
    }


    // the following functions are only used in tests
    public static ArrayPoolList<byte[]> GetFlatEncodedRequests(
        ExecutionRequest[] depositRequests,
        ExecutionRequest[] withdrawalRequests,
        ExecutionRequest[] consolidationRequests
    )
    {
        return new(RequestPartsCount)
        {
            FlatEncodeRequests(depositRequests, depositRequests.Length * DepositRequestsBytesSize),
            FlatEncodeRequests(withdrawalRequests, withdrawalRequests.Length * WithdrawalRequestsBytesSize),
            FlatEncodeRequests(consolidationRequests, consolidationRequests.Length * ConsolidationRequestsBytesSize)
        };

        static byte[] FlatEncodeRequests(ExecutionRequest[] requests, int bufferSize)
        {
            using ArrayPoolList<byte> buffer = new(bufferSize);

            foreach (ExecutionRequest request in requests)
            {
                buffer.AddRange(request.RequestData!);
            }

            return buffer.ToArray();
        }
    }
}
