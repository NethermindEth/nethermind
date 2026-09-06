// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct BuilderExitRequest
{
    public Address SourceAddress { get; set; }

    [SszVector(48)]
    public ReadOnlyMemory<byte> PublicKey { get; set; }

    public static BuilderExitRequest From(ExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestType, (byte)ExecutionRequestType.BuilderExitRequest, nameof(request.RequestType));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestData?.Length ?? 0, ExecutionRequestExtensions.BuilderExitRequestsBytesSize, nameof(request.RequestData));

        ReadOnlyMemory<byte> buffer = request.RequestData;

        return new()
        {
            SourceAddress = new(buffer.Span[0..20]),
            PublicKey = buffer[20..68]
        };
    }

    public readonly ExecutionRequest ToExecutionRequest()
    {
        byte[] result = new byte[ExecutionRequestExtensions.BuilderExitRequestsBytesSize];
        Span<byte> buffer = result;

        SourceAddress.Bytes.CopyTo(buffer); // offset = 0
        PublicKey.Span.CopyTo(buffer[20..]); // offset = 20

        return new()
        {
            RequestType = (byte)ExecutionRequestType.BuilderExitRequest,
            RequestData = result
        };
    }
}
