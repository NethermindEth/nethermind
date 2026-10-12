// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;
using System.Buffers.Binary;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct BuilderDepositRequest
{
    [SszVector(48)]
    public ReadOnlyMemory<byte> PublicKey { get; set; }

    public ValueHash256 WithdrawalCredentials { get; set; }

    public ulong Amount { get; set; }

    [SszVector(96)]
    public ReadOnlyMemory<byte> Signature { get; set; }

    public static BuilderDepositRequest From(ExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestType, (byte)ExecutionRequestType.BuilderDepositRequest, nameof(request.RequestType));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestData?.Length ?? 0, ExecutionRequestExtensions.BuilderDepositRequestsBytesSize, nameof(request.RequestData));

        ReadOnlyMemory<byte> buffer = request.RequestData;
        ReadOnlySpan<byte> span = buffer.Span;

        return new()
        {
            PublicKey = buffer[0..48],
            WithdrawalCredentials = new ValueHash256(span[48..80]),
            Amount = BinaryPrimitives.ReadUInt64LittleEndian(span[80..88]),
            Signature = buffer[88..184]
        };
    }

    public readonly ExecutionRequest ToExecutionRequest()
    {
        byte[] result = new byte[ExecutionRequestExtensions.BuilderDepositRequestsBytesSize];
        Span<byte> buffer = result;

        PublicKey.Span.CopyTo(buffer); // offset = 0
        WithdrawalCredentials.Bytes.CopyTo(buffer[48..]); // offset = 48
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[80..], Amount); // offset = 48 + 32
        Signature.Span.CopyTo(buffer[88..]); // offset = 48 + 32 + 8

        return new()
        {
            RequestType = (byte)ExecutionRequestType.BuilderDepositRequest,
            RequestData = result
        };
    }
}
