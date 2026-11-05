// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Serialization.Ssz;
using System.Buffers.Binary;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct WithdrawalRequest
{
    public Address SourceAddress { get; set; }

    [SszVector(48)]
    public ReadOnlyMemory<byte> ValidatorPublicKey { get; set; }

    public ulong Amount { get; set; }

    public static WithdrawalRequest From(ExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestType, (byte)ExecutionRequestType.WithdrawalRequest, nameof(request.RequestType));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestData?.Length ?? 0, ExecutionRequestExtensions.WithdrawalRequestsBytesSize, nameof(request.RequestData));

        ReadOnlyMemory<byte> buffer = request.RequestData;
        ReadOnlySpan<byte> span = buffer.Span;

        return new()
        {
            SourceAddress = new(span[0..20]),
            ValidatorPublicKey = buffer[20..68],
            Amount = BinaryPrimitives.ReadUInt64LittleEndian(span[68..76])
        };
    }

    public readonly ExecutionRequest ToExecutionRequest()
    {
        byte[] result = new byte[ExecutionRequestExtensions.WithdrawalRequestsBytesSize];
        Span<byte> buffer = result;

        SourceAddress.Bytes.CopyTo(buffer); // offset = 0
        ValidatorPublicKey.Span.CopyTo(buffer[20..]); // offset = 20
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[68..], Amount); // offset = 20 + 48

        return new()
        {
            RequestType = (byte)ExecutionRequestType.WithdrawalRequest,
            RequestData = result
        };
    }
}
