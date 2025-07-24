// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    public const int MaxRequestsCount = 3;
    private const int PublicKeySize = 48;

    public static readonly byte[][] EmptyRequests = [];
    public static readonly Hash256 EmptyRequestsHash = CalculateHashFromFlatEncodedRequests(EmptyRequests);

    [SkipLocalsInit]
    public static Hash256 CalculateHashFromFlatEncodedRequests(byte[][]? flatEncodedRequests)
    {
        // make sure that length is 3 or less elements
        if (flatEncodedRequests is null)
        {
            throw new ArgumentException("Flat encoded requests must be an array");
        }

        using ArrayPoolList<byte> concatenatedHashes = new(Hash256.Size * MaxRequestsCount);
        foreach (byte[] requests in flatEncodedRequests)
        {
            if (requests.Length <= 1) continue;
            concatenatedHashes.AddRange(SHA256.HashData(requests));
        }

        // Compute sha256 of the concatenated hashes
        return new Hash256(SHA256.HashData(concatenatedHashes.UnsafeGetInternalArray().AsSpan(0, concatenatedHashes.Count)));
    }


    // the following functions are only used in tests
    public static ArrayPoolList<byte[]> GetFlatEncodedRequests(
        ExecutionRequest[] depositRequests,
        ExecutionRequest[] withdrawalRequests,
        ExecutionRequest[] consolidationRequests
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
