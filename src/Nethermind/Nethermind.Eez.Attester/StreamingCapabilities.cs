// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Grpc.Core;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingCapabilities(ByteString? identity, AttesterOptions options)
{
    internal CapabilitiesResponse Read()
    {
        if (identity is null) throw new RpcException(new Status(StatusCode.Unimplemented, "capabilities unavailable"));
        return new CapabilitiesResponse
        {
            ProfileId = 2,
            ValidationIdentity = identity,
            MaxWindowBlocks = (ulong)options.Limits.MaxBlocks,
            MaxRequestBytes = (ulong)options.Limits.MaxBytes,
            MaxRequestWitnessItems = (ulong)options.Limits.MaxWitnessItems,
            MaxMessageBytes = (ulong)options.Limits.MaxMessageBytes,
            StreamIdleTimeoutMillis = (ulong)options.StreamIdleTimeout.TotalMilliseconds,
            RequestTimeoutMillis = (ulong)options.RequestTimeout.TotalMilliseconds,
        };
    }
}
