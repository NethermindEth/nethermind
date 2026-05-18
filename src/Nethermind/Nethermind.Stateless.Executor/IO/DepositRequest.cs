// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.ExecutionRequest;
using Nethermind.Serialization.Ssz;
using System.Buffers.Binary;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct DepositRequest
{
    [SszVector(48)]
    public byte[] PublicKey { get; set; }

    public SszBytes32 WithdrawalCredentials { get; set; }

    public ulong Amount { get; set; }

    [SszVector(96)]
    public byte[] Signature { get; set; }

    public ulong Index { get; set; }

    public static DepositRequest From(ExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestType, (byte)ExecutionRequestType.Deposit, nameof(request.RequestType));
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            request.RequestData?.Length ?? 0, ExecutionRequestExtensions.DepositRequestsBytesSize, nameof(request.RequestData));

        ReadOnlySpan<byte> buffer = request.RequestData;

        return new()
        {
            PublicKey = buffer[0..48].ToArray(),
            WithdrawalCredentials = new SszBytes32(buffer[48..80]),
            Amount = BinaryPrimitives.ReadUInt64LittleEndian(buffer[80..88]),
            Signature = buffer[88..184].ToArray(),
            Index = BinaryPrimitives.ReadUInt64LittleEndian(buffer[184..192])
        };
    }

    public readonly ExecutionRequest ToExecutionRequest()
    {
        byte[] result = new byte[ExecutionRequestExtensions.DepositRequestsBytesSize];
        Span<byte> buffer = result;

        PublicKey.CopyTo(buffer); // offset = 0
        WithdrawalCredentials.Hash.Bytes.CopyTo(buffer[48..]); // offset += 48
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[80..], Amount); // offset += 32
        Signature.CopyTo(result, 88); // offset += 8
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[184..], Index); // offset += 96

        return new()
        {
            RequestType = (byte)ExecutionRequestType.Deposit,
            RequestData = result
        };
    }
}
