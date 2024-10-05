// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections.Generic;
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

    public static Hash256 CalculateRoot(this ExecutionRequest[] requests)
    {
        byte[] Hashes = new byte[requests.Length * 32];
        for (int i = 0; i < requests.Length; i++)
        {
            Hash256 hash = new Hash256(requests[i].FlatEncode());
            hash.Bytes.CopyTo(Hashes.AsSpan(i * 32));
        }
        return new Hash256(Hashes);
    }
}
