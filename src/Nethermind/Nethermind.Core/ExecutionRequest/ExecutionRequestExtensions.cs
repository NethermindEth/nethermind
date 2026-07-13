// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.ExecutionRequest;

using SHA256 =
#if ZK_EVM
    ExecutionRequestExtensions.Sha256;
#else
    System.Security.Cryptography.SHA256;
#endif

public static class ExecutionRequestExtensions
{
    public const int PublicKeySize = 48;
    public const int WithdrawalCredentialsSize = Hash256.Size;
    public const int AmountSize = sizeof(ulong);
    public const int SignatureSize = 96;
    public const int IndexSize = sizeof(ulong);
    public const int DepositRequestsBytesSize = PublicKeySize + WithdrawalCredentialsSize + AmountSize + SignatureSize + IndexSize;

    public const int WithdrawalRequestsBytesSize = Address.Size + PublicKeySize /*validator_pubkey: Bytes48*/ + sizeof(ulong) /*amount: uint64*/;
    public const int ConsolidationRequestsBytesSize = Address.Size + PublicKeySize /*source_pubkey: Bytes48*/ + PublicKeySize /*target_pubkey: Bytes48*/;
    public const int BuilderDepositRequestsBytesSize = PublicKeySize + WithdrawalCredentialsSize + AmountSize + SignatureSize;
    public const int BuilderExitRequestsBytesSize = Address.Size + PublicKeySize;
    public const int MaxRequestsCount = 5;

    public static readonly byte[][] EmptyRequests = [];
    public static readonly Hash256 EmptyRequestsHash = CalculateHashFromFlatEncodedRequests(EmptyRequests);

    [SkipLocalsInit]
    public static Hash256 CalculateHashFromFlatEncodedRequests(byte[][]? flatEncodedRequests)
    {
        ArgumentNullException.ThrowIfNull(flatEncodedRequests);

        using ArrayPoolListRef<byte> concatenatedHashes = new(Hash256.Size * MaxRequestsCount);
        foreach (byte[] requests in flatEncodedRequests)
        {
            if (requests.Length <= 1) continue;
            concatenatedHashes.AddRange(SHA256.HashData(requests));
        }

        // Compute sha256 of the concatenated hashes
        return new Hash256(SHA256.HashData(concatenatedHashes.UnsafeGetInternalArray().AsSpan(0, concatenatedHashes.Count)));
    }


    public static ArrayPoolList<byte[]> GetFlatEncodedRequests(
        ExecutionRequest[] depositRequests,
        ExecutionRequest[] withdrawalRequests,
        ExecutionRequest[] consolidationRequests,
        ExecutionRequest[] builderDepositRequests,
        ExecutionRequest[] builderExitRequests
    )
    {
        ArrayPoolList<byte[]> result = new(MaxRequestsCount);

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

        if (builderDepositRequests.Length > 0)
        {
            result.Add(FlatEncodeRequests(builderDepositRequests, builderDepositRequests.Length * BuilderDepositRequestsBytesSize, (byte)ExecutionRequestType.BuilderDepositRequest));
        }

        if (builderExitRequests.Length > 0)
        {
            result.Add(FlatEncodeRequests(builderExitRequests, builderExitRequests.Length * BuilderExitRequestsBytesSize, (byte)ExecutionRequestType.BuilderExitRequest));
        }

        return result;

        static byte[] FlatEncodeRequests(ExecutionRequest[] requests, int bufferSize, byte type)
        {
            using ArrayPoolListRef<byte> buffer = new(bufferSize + 1, type);

            foreach (ExecutionRequest request in requests)
            {
                buffer.AddRange(request.RequestData!);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>
    /// Decodes flat encoded execution request groups into deposit, withdrawal, consolidation,
    /// builder deposit, and builder exit requests.
    /// </summary>
    /// <param name="requests">Flat encoded request groups, each prefixed by an execution request type byte.</param>
    /// <returns>The decoded request groups.</returns>
    public static (
        ExecutionRequest[] DepositRequests,
        ExecutionRequest[] WithdrawalRequests,
        ExecutionRequest[] ConsolidationRequests,
        ExecutionRequest[] BuilderDepositRequests,
        ExecutionRequest[] BuilderExitRequests)
        GetFlatDecodedRequests(byte[][] requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        ExecutionRequest[] depositRequests = [];
        ExecutionRequest[] withdrawalRequests = [];
        ExecutionRequest[] consolidationRequests = [];
        ExecutionRequest[] builderDepositRequests = [];
        ExecutionRequest[] builderExitRequests = [];
        int lastType = -1;

        for (int i = 0; i < requests.Length; i++)
        {
            byte[] encoded = requests[i];

            if (encoded.Length < 1)
                throw new ArgumentException("Empty execution request blob.", nameof(requests));

            byte type = encoded[0];

            if (type <= lastType)
                throw new ArgumentException("Execution requests must be in strict ascending type order.", nameof(requests));

            lastType = type;

            switch ((ExecutionRequestType)type)
            {
                case ExecutionRequestType.Deposit:
                    depositRequests = DecodeRequests(encoded, DepositRequestsBytesSize, type, nameof(ExecutionRequestType.Deposit), nameof(requests));
                    break;
                case ExecutionRequestType.WithdrawalRequest:
                    withdrawalRequests = DecodeRequests(encoded, WithdrawalRequestsBytesSize, type, nameof(ExecutionRequestType.WithdrawalRequest), nameof(requests));
                    break;
                case ExecutionRequestType.ConsolidationRequest:
                    consolidationRequests = DecodeRequests(encoded, ConsolidationRequestsBytesSize, type, nameof(ExecutionRequestType.ConsolidationRequest), nameof(requests));
                    break;
                case ExecutionRequestType.BuilderDepositRequest:
                    builderDepositRequests = DecodeRequests(encoded, BuilderDepositRequestsBytesSize, type, nameof(ExecutionRequestType.BuilderDepositRequest), nameof(requests));
                    break;
                case ExecutionRequestType.BuilderExitRequest:
                    builderExitRequests = DecodeRequests(encoded, BuilderExitRequestsBytesSize, type, nameof(ExecutionRequestType.BuilderExitRequest), nameof(requests));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(requests), type, "Unknown execution request type.");
            }
        }

        return (depositRequests, withdrawalRequests, consolidationRequests, builderDepositRequests, builderExitRequests);

        static ExecutionRequest[] DecodeRequests(byte[] encodedRequests, int requestDataSize, byte type, string typeName, string parameterName)
        {
            ReadOnlySpan<byte> requestData = encodedRequests.AsSpan(1);

            if (requestData.Length % requestDataSize != 0)
                throw new ArgumentException($"Invalid {typeName} request payload length.", parameterName);

            if (requestData.Length == 0)
                return [];

            ExecutionRequest[] result = new ExecutionRequest[requestData.Length / requestDataSize];

            for (int offset = 0, requestIndex = 0; offset < requestData.Length; offset += requestDataSize, requestIndex++)
            {
                result[requestIndex] = new()
                {
                    RequestType = type,
                    RequestData = requestData.Slice(offset, requestDataSize).ToArray()
                };
            }

            return result;
        }
    }

#if ZK_EVM
    internal static class Sha256
    {
        internal static byte[] HashData(ReadOnlySpan<byte> data)
        {
            byte[] output = new byte[System.Security.Cryptography.SHA256.HashSizeInBytes];

            Nethermind.Zkvm.Abstractions.Accelerators.Sha256(data, output);

            return output;
        }
    }
#endif
}
