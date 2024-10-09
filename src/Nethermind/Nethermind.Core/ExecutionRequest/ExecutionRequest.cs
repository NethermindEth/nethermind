// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Security.Cryptography;
using System.Linq;
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

    public byte[] FlatEncode()
    {
        byte[] encoded = new byte[RequestData!.Length + 1];
        encoded[0] = RequestType;
        RequestData.CopyTo(encoded, 1);
        return encoded;
    }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(ExecutionRequest)}
            {{{nameof(RequestType)}: {RequestType},
            {nameof(RequestData)}: {RequestData?.ToHexString()}}}";
}

public static class ExecutionRequestExtensions
{
    public static byte[] FlatEncode(this ExecutionRequest[] requests)
    {
        List<byte> encoded = new();
        foreach (ExecutionRequest request in requests)
        {
            encoded.AddRange(request.FlatEncode());
        }
        return encoded.ToArray();
    }

    public static byte[] FlatEncodeWithoutType(this ExecutionRequest[] requests)
    {
        List<byte> encoded = new();
        foreach (ExecutionRequest request in requests)
        {
            encoded.AddRange(request.RequestData!);
        }
        return encoded.ToArray();
    }

    public static Hash256 CalculateHash(this IEnumerable<ExecutionRequest> requests)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            using (SHA256 sha256Inner = SHA256.Create())
            {
                foreach (ExecutionRequest request in requests)
                {
                    byte[] requestHash = sha256Inner.ComputeHash(request.FlatEncode());

                    // Update the outer hash with the result of each inner hash
                    sha256.TransformBlock(requestHash, 0, requestHash.Length, null, 0);
                }
                // Complete the final hash computation
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                return new Hash256(sha256.Hash!);
            }
        }
    }

    public static Hash256 CalculateHash(this ExecutionRequest[] requests)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            using (SHA256 sha256Inner = SHA256.Create())
            {
                foreach (ExecutionRequest request in requests)
                {
                    byte[] requestHash = sha256Inner.ComputeHash(request.FlatEncode());

                    // Update the outer hash with the result of each inner hash
                    sha256.TransformBlock(requestHash, 0, requestHash.Length, null, 0);
                }
                // Complete the final hash computation
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                return new Hash256(sha256.Hash!);
            }
        }
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
