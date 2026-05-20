// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct ConsolidationRequest
{
    public Address SourceAddress { get; set; }

    [SszVector(48)]
    public ReadOnlyMemory<byte> ValidatorPublicKey { get; set; }

    [SszVector(48)]
    public ReadOnlyMemory<byte> TargetPublicKey { get; set; }

    public static ConsolidationRequest From(ExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestType, (byte)ExecutionRequestType.ConsolidationRequest, nameof(request.RequestType));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestData?.Length ?? 0, ExecutionRequestExtensions.ConsolidationRequestsBytesSize, nameof(request.RequestData));

        ReadOnlyMemory<byte> buffer = request.RequestData;

        return new()
        {
            SourceAddress = new(buffer.Span[0..20]),
            ValidatorPublicKey = buffer[20..68],
            TargetPublicKey = buffer[68..116]
        };
    }

    public readonly ExecutionRequest ToExecutionRequest()
    {
        byte[] result = new byte[ExecutionRequestExtensions.ConsolidationRequestsBytesSize];
        Span<byte> buffer = result;

        SourceAddress.Bytes.CopyTo(buffer); // offset = 0
        ValidatorPublicKey.Span.CopyTo(buffer[20..]); // offset = 20
        TargetPublicKey.Span.CopyTo(buffer[68..]); // offset = 20 + 48

        return new()
        {
            RequestType = (byte)ExecutionRequestType.ConsolidationRequest,
            RequestData = result
        };
    }
}
